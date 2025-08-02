#if DEBUG
//#define TEST_MULTIPLE_INDICES_PAGES
#endif

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

namespace GTPSPVolTools.Packing;

public class VolumeBuilder
{
    public const int BTREE_MAX_PAGE_SIZE = 0x1000;
    public const int BLOCK_SIZE = 0x40;
    public const int DATA_SECTOR_SIZE = 0x800;
    public const int DATA_CHUNK_SIZE_FOR_INSTALL = 0x10000;

    public string InputFolder { get; set; }

    private VolumeEntry RootDir { get; set; }

    /// <summary>
    /// List of all directory as entries we are writing
    /// </summary>
    private readonly List<VolumeEntry> _allDirEntries = [];

    /// <summary>
    /// All written btree pages, including index and entry pages
    /// </summary>
    private readonly List<PageHolderBase> _allPages = [];

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

        _allDirEntries.Add(RootDir);
        TraverseBuildEntryPackList(RootDir);

        // This will generate index page for other index pages since there are a lot of files
        // Testing purposes
#if TEST_MULTIPLE_INDICES_PAGES
        for (int i = 0; i < 30000; i++)
        {
            RootDir.Child.Add(new VolumeEntry() { Name = $"z{i,-0x78:X8}", Type = VolumeEntry.EntryType.File });
        }
#endif
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
#if DEBUG
            File.WriteAllBytes("volume_toc_header_built.bin", toc);
#endif

            VolumeCrypto.EncryptHeaderPart(toc[0x04..], toc[0x04..], toc.Length - 4); // Magic is not encrypted
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
            volStream.BaseStream.Align(DATA_CHUNK_SIZE_FOR_INSTALL, grow: true);
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
        // Create our btree & its pages
        for (int i = 0; i < _allDirEntries.Count; i++)
            BuildPagesForFolder(_allDirEntries[i]);

        // Write page entries
        BitStream tocStream = new BitStream(endian: BitStreamSignificantBitOrder.MSB);

        // Write pages
        tocStream.Position = _allPages.Count * sizeof(ushort);
        tocStream.Align(BLOCK_SIZE);

        int lastPageOffset = tocStream.Position;
        for (int i = 0; i < _allPages.Count; i++)
        {
            tocStream.Position = lastPageOffset;
            int pageOffset = tocStream.Position;
            _allPages[i].Write(ref tocStream);

            lastPageOffset = tocStream.Position;
            tocStream.Position = i * sizeof(ushort);

            uint pageBlockOffset = (uint)(pageOffset / BLOCK_SIZE);
            Debug.Assert(pageBlockOffset <= ushort.MaxValue, "Page block offset exceeds ushort max value. This volume has too many pages.");

            tocStream.WriteUInt16(pageBlockOffset);
        }
        Debug.Assert((uint)(lastPageOffset / BLOCK_SIZE) <= ushort.MaxValue, "Last page block offset exceeds ushort max value. This volume has too many pages.");
        tocStream.WriteUInt16((ushort)(lastPageOffset / BLOCK_SIZE)); // Terminator

        // Write volume header, append toc writen above to it
        BitStream headerStream = new BitStream(endian: BitStreamSignificantBitOrder.MSB);
        uint serial = (uint)(DateTime.UtcNow - new DateTime(2001, 1, 1)).TotalSeconds;
        headerStream.WriteUInt32(Volume.VolumeMagic);
        headerStream.WriteUInt32(0xDEADBEEF);
        headerStream.WriteUInt32(serial);
        headerStream.WriteUInt32(0);
        headerStream.WriteUInt32(0); // Toc Block Offset - 0 means it's at 0x800 (1 * 0x800) + (offset aka 0 * 0x800)

        const int baseTocPos = 0x800;
        headerStream.Position = baseTocPos;
        headerStream.WriteByteData(tocStream.GetBuffer());
        headerStream.Align(DATA_SECTOR_SIZE);
        int fileDataOffset = headerStream.Position;

        long totalDataSize = new FileInfo("gtfiles.temp").Length;
        uint totalDataSizeAligned = MiscUtils.AlignValue((uint)totalDataSize, DATA_CHUNK_SIZE_FOR_INSTALL);

        headerStream.Position = 0x14;
        headerStream.WriteUInt32((uint)(fileDataOffset - baseTocPos) / DATA_SECTOR_SIZE); // FileDataSectorOffset
        headerStream.WriteUInt32((uint)_allPages.Count + 1u); // Num page offsets, +1 for terminator
        headerStream.WriteUInt32((uint)tocStream.Length); // ToC Length
        headerStream.WriteUInt32(totalDataSizeAligned / DATA_CHUNK_SIZE_FOR_INSTALL); // Chunked data size for install

        return headerStream.GetBuffer();
    }

    /// <summary>
    /// Writes one or multiple pages for a directory entry.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="folder"></param>
    private void BuildPagesForFolder(VolumeEntry folder)
    {
        int entryIndex = 0;

        List<EntryPageHolder> entryPages = [];
        EntryPageHolder currentEntryPage = new EntryPageHolder();
        entryPages.Add(currentEntryPage);

        while (entryIndex < folder.Child.Count)
        {
            // Calculate if it can be writen in the current page first
            VolumeEntry entry = folder.Child[entryIndex];
            if (!currentEntryPage.TryAddEntry(entry))
            {
                currentEntryPage = new EntryPageHolder();
                entryPages.Add(currentEntryPage);

                currentEntryPage.TryAddEntry(entry);
            }

            entryIndex++;
        }

        List<IndexPageHolder> indexPages = [];
        List<IndexEntry> allIndices = [];
        if (entryPages.Count > 1)
        {
            IndexPageHolder idxPage = new IndexPageHolder();
            indexPages.Add(idxPage);
            foreach (var page in entryPages)
            {
                // Add the first entry of each page to the current index page
                var newIndexEntry = new IndexEntry() { IndexerString = page.Entries[0].Name, SubPageIndex = page.Entries[0].SubPageIndex };
                if (!idxPage.TryAddEntry(newIndexEntry))
                {
                    idxPage = new IndexPageHolder();
                    indexPages.Add(idxPage);

                    idxPage.TryAddEntry(newIndexEntry);
                }

                allIndices.Add(newIndexEntry);
            }
        }

        ushort startPage = (ushort)_allPages.Count;
        Debug.Assert(startPage <= (int)Utils.GetMaxValueForBitCount(14), $"Exceeded maximum page index ({Utils.GetMaxValueForBitCount(14)}). This volume has too many files/folders.");

        // Build parent index pages of index pages (this should only happen if there are a LOT of files in a folder that a single index page cannot hold them all)
        // pretty proud of this part lol
        List<IndexPageHolder> currentIndexPages = indexPages;
        ushort currentPageCounter = startPage;
        while (currentIndexPages.Count > 1)
        {
            var parentIndexPages = new List<IndexPageHolder>();
            var currentParentIndexPage = new IndexPageHolder();
            parentIndexPages.Add(currentParentIndexPage);

            // Easier part, register new index from child index pages
            for (int i = 0; i < currentIndexPages.Count; i++)
            {
                var newIndexEntry = new IndexEntry() { IndexerString = currentIndexPages[i].Entries[0].IndexerString, SubPageIndex = currentPageCounter };
                if (!currentParentIndexPage.TryAddEntry(newIndexEntry))
                {
                    currentParentIndexPage = new IndexPageHolder();
                    parentIndexPages.Add(currentParentIndexPage);

                    currentParentIndexPage.TryAddEntry(newIndexEntry);
                }
                currentPageCounter++;
            }
            currentPageCounter = 0;

            Console.WriteLine($"Adding {parentIndexPages.Count} parents");

            // Insert Index pages indexing other index pages at the front
            indexPages.InsertRange(0, parentIndexPages);

            // Retroactively update page indices of child pages
            for (int i = 0; i < indexPages.Count; i++)
            {
                foreach (var entry in indexPages[i].Entries)
                {
                    // Note: this will set an incorrect index for index pages that actually link to their directory page.
                    // That'll be fixed in another pass
                    Debug.Assert(entry.SubPageIndex + parentIndexPages.Count < (int)Utils.GetMaxValueForBitCount(14));

                    entry.SubPageIndex += (ushort)parentIndexPages.Count;
                }
            }

            currentIndexPages = parentIndexPages;
        }

        // Link index entries that point to their actual entry to their entry page
        if (indexPages.Count > 0)
        {
            for (int i = 0; i < allIndices.Count; i++)
                allIndices[i].SubPageIndex = (ushort)(startPage + (indexPages.Count + i));
        }

        folder.SubPageIndex = startPage;
        _allPages.AddRange(indexPages);
        _allPages.AddRange(entryPages);
    }

    /// <summary>
    /// Writes all the file content for the volume as a temporary file.
    /// </summary>
    private void WriteFiles()
    {
        using var fs = new FileStream("gtfiles.temp", FileMode.Create);
        using var bs = new BinaryStream(fs);

        int i = 1;
        int count = _allDirEntries.Count(c => c.Type != VolumeEntry.EntryType.Directory);

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

#if TEST_MULTIPLE_INDICES_PAGES
                if (!File.Exists(Path.Combine(InputFolder, filePath)))
                    continue;
#endif
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

                fileWriter.Align(BLOCK_SIZE, grow: true);
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

            if (!relativePath.All(static c => char.IsAscii(c)))
                throw new Exception($"Invalid character in path: {relativePath}. Path must be ASCII characters.");

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
                _allDirEntries.Add(entry);
                TraverseBuildEntryPackList(entry);
            }
        }
    }

    /// <summary>
    /// Determines whether a specific path must be marked as a compressable volume entry.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    private static bool IsCompressableFile(string path)
    {
        string volPath = path.Replace('\\', '/');

        if (volPath.StartsWith("sound_gt") || volPath.StartsWith("carsound") ||
            volPath.EndsWith(".at3") || volPath.EndsWith(".sgd") || volPath.EndsWith("esgx")) // No sound files - bit compressed
            return false;

        if (volPath.StartsWith("movie") || volPath.EndsWith(".pmf")) // Same
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
