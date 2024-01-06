using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Buffers.Binary;

using System.IO;
using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;
using System.Diagnostics;

using PDTools.Utils;

namespace GTPSPVolTools.Packing
{
    public class VolumeBuilder
    {
        public const int BlockSize = 0x800;
        public const int BTREE_MAX_SEGMENT_SIZE = 0x1000;
        public const int FileAlignment = 0x40;

        public string InputFolder { get; set; }

        private VolumeEntry RootDir { get; set; }

        /// <summary>
        /// List of all directory as entries we are writing
        /// </summary>
        private List<VolumeEntry> _entries = new();

        /// <summary>
        /// Offsets to segments for the toc
        /// </summary>
        private List<uint> _segmentOffsets = new();

        /// <summary>
        /// Last written segment index
        /// </summary>
        private ushort _lastSegmentIndex;

        /// <summary>
        /// Imports and registers the specified local directory and all of its files before building a volume.
        /// </summary>
        /// <param name="inputFolder"></param>
        /// <param name="outputFile"></param>
        public void RegisterFilesToPack(string inputFolder)
        {
            Console.WriteLine($"Indexing '{Path.GetFullPath(inputFolder)}' to find files to pack.. ");
            InputFolder = Path.GetFullPath(inputFolder);

            RootDir = new VolumeEntry();
            Import(RootDir, InputFolder);

            _entries.Add(RootDir);
            TraverseBuildEntryPackList(RootDir);
        }

        /// <summary>
        /// Builds the volume to a specified file.
        /// </summary>
        /// <param name="outputFile"></param>
        public void Build(string outputFile)
        {
            Console.WriteLine("Building volume.");

            using var fsVol = new FileStream(outputFile, FileMode.Create);
            using var volStream = new BinaryStream(fsVol);

            try
            {
                /* The strategy is to first write the file content into a temp file
                 * since the ToC is using 7 bit encoded ints for the uncompressed and compressed size, 
                 * it's impossible to write the toc first
                 * The temp file will be merged to the toc later on */

                WriteFiles();

                // Write all the file & directory entries
                Span<byte> toc = WriteToC();
                VolumeCrypto.EncryptHeaderPart(toc, toc.Slice(0x04), toc.Length - 4); // Magic is not encrypted
                fsVol.Write(toc);
                
                // Merge toc and file blob.
                using var fs = new FileStream("gtfiles.temp", FileMode.Open);
                Console.WriteLine($"Merging Data and ToC... ({Utils.BytesToString(fs.Length)})");

                int count = 0;
                byte[] buffer = new byte[0x20000];
                while ((count = fs.Read(buffer, 0, buffer.Length)) > 0)
                    volStream.BaseStream.Write(buffer, 0, count);

                // Just incase. There's a value in the header with the number of 0x10000 data chunks
                // Original is encrypted padding, personally I say we don't care for it
                volStream.BaseStream.Align(0x10000, grow: true);
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occured while building volume: {e}");
            }
            finally
            {
                if (File.Exists("gtfiles.temp"))
                    File.Delete("gtfiles.temp");
            }

            Console.WriteLine($"Done, folder packed to {Path.GetFullPath(outputFile)}.");
        }

        /// <summary>
        /// Writes the volume's table of contents.
        /// </summary>
        /// <returns></returns>
        private Span<byte> WriteToC()
        {
            // Write segment entries
            BitStream tocSegmentsStream = new BitStream(BitStreamMode.Write, endian: BitStreamSignificantBitOrder.MSB);
            for (int i = 0; i < _entries.Count; i++)
            {
                var dir = _entries[i];
                dir.EntriesLocationSegmentIndex = _lastSegmentIndex;
                WriteDirEntryToCSegment(ref tocSegmentsStream, _entries[i]);
            }

            // Link segments by rewriting the dir entries with the actual segment index they link to
            for (int i = 1; i < _entries.Count; i++)
            {
                var dir = _entries[i];

                tocSegmentsStream.Position = (int)_segmentOffsets[dir.DirDefinitionSegmentIndex] + dir.EntryOffset;
                tocSegmentsStream.WriteBoolBit(true); // Dir
                tocSegmentsStream.WriteBoolBit(false); // Uncomp
                tocSegmentsStream.WriteBits((ulong)(dir.EntriesLocationSegmentIndex >> 8), 6); // Major
                tocSegmentsStream.WriteVarPrefixString(dir.Name);
                tocSegmentsStream.WriteByte((byte)(dir.EntriesLocationSegmentIndex & 0xFF)); // Minor
            }

            // Write segment offset toc, append entry toc to it
            BitStream tocStream = new BitStream(BitStreamMode.Write, endian: BitStreamSignificantBitOrder.MSB);
            tocStream.Position = _segmentOffsets.Count * sizeof(ushort);
            tocStream.Align(0x40);
            int tocOffsetSize = tocStream.Position;

            tocStream.Position = 0;
            for (int i = 0; i < _segmentOffsets.Count; i++)
                tocStream.WriteUInt16((ushort)((uint)(tocOffsetSize + _segmentOffsets[i]) / 0x40));
            tocStream.Position += 2; // Make way for terminator
            tocStream.Align(0x40);
            tocStream.WriteByteData(tocSegmentsStream.GetBuffer());

            ushort segmentTerminator = (ushort)(tocStream.Length / 0x40);
            tocStream.Position = _segmentOffsets.Count * sizeof(ushort);
            tocStream.WriteUInt16(segmentTerminator);

            // Write volume header, append toc writen above to it
            BitStream headerStream = new BitStream(BitStreamMode.Write, endian: BitStreamSignificantBitOrder.MSB);
            uint serial = (uint)(DateTime.UtcNow - new DateTime(2001, 1, 1)).TotalSeconds;
            headerStream.WriteUInt32(Volume.VolumeMagic);
            headerStream.WriteUInt32(0xDEADBEEF);
            headerStream.WriteUInt32(serial);
            headerStream.WriteUInt32(0);
            headerStream.WriteUInt32(0); // Toc Block Offset - 0 means it's at 0x800 (1 * 0x800) + (offset * 0x800)

            const int baseTocPos = 0x800;
            headerStream.Position = baseTocPos;
            headerStream.WriteByteData(tocStream.GetBuffer());
            headerStream.Align(BlockSize);
            int fileDataOffset = headerStream.Position;

            long totalDataSize = new FileInfo("gtfiles.temp").Length;
            uint totalDataSize0x10000Chunks = MiscUtils.AlignValue((uint)totalDataSize, 0x10000);

            headerStream.Position = 0x14;
            headerStream.WriteUInt32((uint)(fileDataOffset - baseTocPos) / BlockSize);
            headerStream.WriteUInt32((uint)_segmentOffsets.Count + 1u);
            headerStream.WriteUInt32((uint)tocStream.Length);
            headerStream.WriteUInt32(totalDataSize0x10000Chunks / 0x10000);

            return headerStream.GetBuffer();
        }

        /// <summary>
        /// Writes one or multiple segments for a directory entry.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="folder"></param>
        private void WriteDirEntryToCSegment(ref BitStream stream, VolumeEntry folder)
        {
            int entryIndex = 0;

            BitStream folderSegmentWriter = new BitStream(BitStreamMode.Write, endian: BitStreamSignificantBitOrder.MSB);
            IndexWriter indexWriter = new IndexWriter();
            List<uint> entrySegmentOffsets = new List<uint>(); // For the main vol segment offsets header

            while (entryIndex < folder.Child.Count)
            {
                int segmentKeyCount = 0;
                int segmentStartIndex = entryIndex;

                int baseSegPos = folderSegmentWriter.Position;

                // To keep track of where the keys are located to write them in the toc
                List<int> segmentKeyOffsets = new List<int>();
                BitStream entryWriter = new BitStream(BitStreamMode.Write, 1024, BitStreamSignificantBitOrder.MSB);

                VolumeEntry firstEntry = folder.Child[entryIndex];

                entrySegmentOffsets.Add((uint)folderSegmentWriter.Position);

                while (entryIndex < folder.Child.Count)
                {
                    // Calculate if it can be writen in the current segment first
                    VolumeEntry entry = folder.Child[entryIndex];
                    uint keySize = entry.GetSerializedKeySize();

                    int currentSizeTaken = Utils.MeasureBytesTakenByBits(12 + (segmentKeyCount * 12));
                    currentSizeTaken += entryWriter.Position; // And size of key data themselves

                    if (currentSizeTaken + (keySize + 2) >= BTREE_MAX_SEGMENT_SIZE) // Extra 2 to fit the 12 bits offset as short
                        break; // Entry cannot be written, exit loop to create new segment

                    // To build up the segment's TOC when its filled or done - first one is not included
                    if (segmentKeyCount > 0)
                        segmentKeyOffsets.Add(entryWriter.Position);

                    // For linking, later on
                    if (entry.Type == VolumeEntry.EntryType.Directory)
                    {
                        entry.EntryOffset = entryWriter.Position;
                        entry.DirDefinitionSegmentIndex = _lastSegmentIndex;
                    }

                    // Serialize the key
                    entry.Serialize(ref entryWriter);
                    //entry.EntriesLocationSegmentIndex = _lastSegmentIndex;

                    // Move on to next
                    segmentKeyCount++;
                    entryIndex++;
                }

                // We need to write an index block as the entries did not fit within 0x1000
                if (entryIndex < folder.Child.Count || !indexWriter.IsEmpty)
                {
                    if (indexWriter.IsEmpty)
                        _lastSegmentIndex++;

                    firstEntry.EntriesLocationSegmentIndex = _lastSegmentIndex; // + 1 due to index being behind
                    indexWriter.AddIndex(firstEntry);
                }

                // Finish up segment header
                folderSegmentWriter.Position = baseSegPos;
                folderSegmentWriter.WriteBoolBit(false); // Not index block
                folderSegmentWriter.WriteBits((ulong)segmentKeyCount, 11);

                int segTocSize = Utils.MeasureBytesTakenByBits(12 + (segmentKeyOffsets.Count * 12));
                if (segmentKeyCount > 1) // If theres only one entry, we don't need a toc at all
                {
                    for (int i = 0; i < segmentKeyOffsets.Count; i++)
                    {
                        // Translate each key offset to segment relative offsets
                        folderSegmentWriter.WriteBits((ulong)(segTocSize + segmentKeyOffsets[i]), 12);
                    }
                    folderSegmentWriter.AlignToNextByte();
                }

                // Ensure to translate the entry offsets with the toc size (for linking)
                for (int i = segmentStartIndex; i < entryIndex; i++)
                {
                    var entry = folder.Child[i];
                    if (entry.Type == VolumeEntry.EntryType.Directory)
                        entry.EntryOffset += segTocSize;
                }

                // Write key data
                folderSegmentWriter.WriteByteData(entryWriter.GetSpan());
                folderSegmentWriter.Align(0x40);

                _lastSegmentIndex++;
            }

            // Write index segment (if exists)
            if (!indexWriter.IsEmpty)
            {
                stream.AlignToNextByte();

                _segmentOffsets.Add((uint)stream.Position);
                indexWriter.Write(ref stream);
            }

            // Then folder segments
            // Translate to entry offsets segment relative offsets
            for (int i = 0; i < entrySegmentOffsets.Count; i++)
                _segmentOffsets.Add((uint)(stream.Position + entrySegmentOffsets[i]));

            stream.WriteByteData(folderSegmentWriter.GetSpan()); // Appends all the segments to the folder writer
        }

        /// <summary>
        /// Writes all the file content for the volume as a temporary file.
        /// </summary>
        private void WriteFiles()
        {
            using var fs = new FileStream("gtfiles.temp", FileMode.Create);
            using var bs = new BinaryStream(fs);

            int i = 1;
            int count = _entries.Count(c => c.Type != VolumeEntry.EntryType.Directory);

            WriteDirectoryFileContents(bs, RootDir, "", ref i, ref count);
        }

        /// <summary>
        /// Writes all the file content for a directory.
        /// </summary>
        /// <param name="fileWriter"></param>
        /// <param name="parentDir"></param>
        /// <param name="path"></param>
        /// <param name="currentIndex"></param>
        /// <param name="count"></param>
        private void WriteDirectoryFileContents(BinaryStream fileWriter, VolumeEntry parentDir, string path, ref int currentIndex, ref int count)
        {
            foreach (var entry in parentDir.Child)
            {
                if (entry.Type == VolumeEntry.EntryType.Directory)
                {
                    string subPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                    WriteDirectoryFileContents(fileWriter, entry, subPath, ref currentIndex, ref count);
                }
                else if (entry.Type == VolumeEntry.EntryType.File)
                {
                    string filePath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                    entry.FileOffset = (uint)fileWriter.Position;

                    using var file = File.Open(Path.Combine(InputFolder, filePath), FileMode.Open);
                    long fileSize = file.Length;
                    if (fileSize > int.MaxValue) // int to be safe rather than uint
                        throw new Exception($"File '{filePath}' is too large to write in the volume. ({fileSize} bytes)");
#if DEBUG
                    Console.WriteLine($"Writing {filePath} -> {entry.FileOffset:X8} (Size: {entry.UncompressedSize:X8})");
#endif
                    if (entry.Compressed)
                    {
                        if (fileSize >= 1_024_000 || currentIndex % 100 == 0)
                            Console.WriteLine($"Compressing: {filePath} [{Utils.BytesToString(fileSize)}] ({currentIndex}/{count})");

                        uint compressedSize = Compression.PS2ZIPCompressEncrypt(file, fileWriter);
                        entry.CompressedSize = compressedSize;
                    }
                    else
                    {
                        if (fileSize >= 1_024_000 || currentIndex % 100 == 0)
                            Console.WriteLine($"Writing: {filePath} [{Utils.BytesToString(fileSize)}] ({currentIndex}/{count})");
                        Compression.Encrypt(file, fileWriter);
                    }

                    fileWriter.Align(FileAlignment, grow: true);
                    currentIndex++;
                }

                Debug.Assert(fileWriter.Length < uint.MaxValue, "Volume is too big.");
            }
        }

        /// <summary>
        /// Imports a local file directory as a game directory node.
        /// </summary>
        /// <param name="parent"></param>
        /// <param name="folder"></param>
        private void Import(VolumeEntry parent, string folder)
        {
            var dirEntries = Directory.EnumerateFileSystemEntries(folder)
                .OrderBy(e => e, StringComparer.Ordinal).ToList();

            foreach (var path in dirEntries)
            {
                VolumeEntry entry = new VolumeEntry();
                entry.Name = Path.GetFileName(path);

                string relativePath = path.Substring(folder.Length + 1);

                if (relativePath == "files.txt")
                    continue; // Exclude

                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    entry.Type = VolumeEntry.EntryType.Directory;
                    Import(entry, path);
                }
                else
                {
                    string absolutePath = Path.Combine(folder, relativePath);
                    string volumePath = absolutePath.Substring(InputFolder.Length + 1);

                    var fInfo = new FileInfo(absolutePath);
                    entry.Type = VolumeEntry.EntryType.File;
                    entry.UncompressedSize = (uint)fInfo.Length;
                    entry.Compressed = IsCompressableFile(volumePath);
                }

                parent.Child.Add(entry);
            }
        }

        /// <summary>
        /// Builds the 2D representation of the file system, for packing.
        /// </summary>
        /// <param name="parentDir"></param>
        private void TraverseBuildEntryPackList(VolumeEntry parentDir)
        {
            foreach (var entry in parentDir.Child)
            {
                if (entry.Type == VolumeEntry.EntryType.Directory)
                {
                    _entries.Add(entry);
                    TraverseBuildEntryPackList(entry);
                }
            }
        }

        /// <summary>
        /// Determines whether a specific path must be marked as a compressable volume entry.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private bool IsCompressableFile(string path)
        {
            string volPath = path.Replace('\\', '/');

            if (volPath.StartsWith("sound_gt") || volPath.StartsWith("carsound")) // No sound files - bit compressed
                return false;

            if (volPath.StartsWith("movie")) // Same
                return false;

            if (volPath.StartsWith("replay")) // Already compressed
                return false;

            if (volPath.Equals("piece_gt5m/env/env2.txs")) // Possibly same
                return false;

            if (volPath.EndsWith(".tbd") || volPath.EndsWith(".png")) // PartsInfo + RaceInfo, pngs are already compressed
                return false;

            if (volPath.Equals("crs/race.mdl")) // ?
                return false;

            return true;
        }
    }
}
