using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO;
using Syroot.BinaryData.Memory;

using PDTools.Utils;

namespace GTPSPVolTools;

public class Volume
{
    private const int MainHeaderBlockSize = 1;
    public const int BlockSize = 0x800;

    public const int VolumeMagic = 0x71D319F3;

    public long SerialDate { get; set; }

    /// <summary>
    /// Offset of the toc in blocks, starting from header
    /// </summary>
    public int ToCBlockOffset { get; set; }

    /// <summary>
    /// From start of toc end
    /// </summary>
    public int FileDataBlockOffset { get; set; }

    /// <summary>
    /// Number of folders in the file system
    /// </summary>
    public int FolderCount { get; set; }

    /// <summary>
    /// In actual bytes
    /// </summary>
    public int ToCLength { get; set; }

    /// <summary>
    /// Data size as a number of 0x10000 chunks, starting from data offset.
    /// </summary>
    public int TotalDataSize { get; set; }

    public long ToCActualOffset { get; set; }
    public long DataActualOffset { get; set; }

    public List<ushort> FolderOffsets { get; set; }

    private List<VolumeEntry> _files { get; set; } = [];

    private string _fileName;
    private FileStream _fs;

    public Volume(string fileName)
    {
        _fileName = fileName;
    }

    // 8b12d6c
    public bool Init(bool saveVolumeHeaderToc = false)
    {
        _fs = File.Open(_fileName, FileMode.Open);

        Span<byte> buf = new byte[0x40];
        _fs.ReadExactly(buf);

        SpanReader sr = new SpanReader(buf);
        int Magic = sr.ReadInt32();

        // NOTE: GT PSP Also supports '0x515111d3' magic, which makes the volume behave slightly differently.
        // Header is a bit different (refer to 0x8b12d6c aka Volume::InitHeader)
        // data fetching aswell (refer to 0x8b131c4 aka Volume::GetDirectoryHeaderPtrByIndex(Volume *this, uint folderIndex, byte param_3)
        // ^ UCES01245 2.00
        if (Magic != VolumeMagic) 
        {
            Console.WriteLine("Volume: Magic did not match. Not a volume file.");
            return false;
        }

        VolumeCrypto.DecryptHeaderPart(buf[0x04..], buf[0x04..], sizeof(uint));
        VolumeCrypto.DecryptHeaderPart(buf[0x08..], buf[0x08..], sizeof(long));
        VolumeCrypto.DecryptHeaderPart(buf[0x10..], buf[0x10..], sizeof(uint)); // Block Size
        VolumeCrypto.DecryptHeaderPart(buf[0x14..], buf[0x14..], sizeof(uint));
        VolumeCrypto.DecryptHeaderPart(buf[0x18..], buf[0x18..], sizeof(uint));
        VolumeCrypto.DecryptHeaderPart(buf[0x1c..], buf[0x1c..], sizeof(uint));
        VolumeCrypto.DecryptHeaderPart(buf[0x20..], buf[0x20..], sizeof(uint));

        uint deadbeef = sr.ReadUInt32(); // Unused
        SerialDate = sr.ReadUInt32();
        int unk3 = sr.ReadInt32(); // Unused
        ToCBlockOffset = sr.ReadInt32();
        FileDataBlockOffset = sr.ReadInt32();
        FolderCount = sr.ReadInt32();
        ToCLength = sr.ReadInt32();
        TotalDataSize = sr.ReadInt32();

        // Calculate offsets
        ToCActualOffset = (MainHeaderBlockSize + ToCBlockOffset) * BlockSize;
        DataActualOffset = (MainHeaderBlockSize + ToCBlockOffset + FileDataBlockOffset) * BlockSize;

        _fs.Position = ToCActualOffset;

        Console.WriteLine($"Volume Header:");
        Console.WriteLine($"- Serial Date: {SerialDate} ({new DateTime(2001, 1, 1) + TimeSpan.FromSeconds(SerialDate)})");
        Console.WriteLine($"- ToC Block Offset: {ToCBlockOffset}");
        Console.WriteLine($"- File Data Block Offset: {FileDataBlockOffset}");
        Console.WriteLine($"- Folder Count: {FolderCount}");
        Console.WriteLine($"- ToC Length: 0x{ToCLength:X8}");
        Console.WriteLine($"- Num Data Chunks: 0x{TotalDataSize:X8} (0x{TotalDataSize:X8} * 0x10000 = Total Data Size is {TotalDataSize * 0x10000:X8}/{TotalDataSize * 0x10000} bytes)");
        Console.WriteLine($"- Actual ToC Offset: 0x{ToCActualOffset:X8}");
        Console.WriteLine($"- Actual Data Start Offset: 0x{DataActualOffset:X8}");

        if (saveVolumeHeaderToc)
        {
            _fs.Position = 0;
            Span<byte> wholeThing = new byte[ToCActualOffset + ToCLength];
            _fs.ReadExactly(wholeThing);
            VolumeCrypto.DecryptHeaderPart(wholeThing, wholeThing, (int)(ToCActualOffset + ToCLength));
            File.WriteAllBytes("volume_toc_header.bin", wholeThing.ToArray());
        }

        _fs.Position = ToCActualOffset;
        Span<byte> folderCountBuf = new byte[(FolderCount * sizeof(ushort)) + 2];
        _fs.ReadExactly(folderCountBuf);
        VolumeCrypto.DecryptHeaderPart(folderCountBuf, folderCountBuf, folderCountBuf.Length);

        SpanReader folderBufReader = new SpanReader(folderCountBuf);
        FolderOffsets = new List<ushort>(FolderCount + 1);
        for (int i = 0; i < FolderCount + 1; i++)
            FolderOffsets.Add(folderBufReader.ReadUInt16());

        return true;
    }

    /// <summary>
    /// Finds an entry in the volume by path.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    // 8b13568
    private VolumeEntry Find(string path)
    {
        Span<byte> parentEntry = GetFolderPtr(0, 0); // Root

        var symbols = path.Split('/');

        Span<byte> entryData = default;
        Span<byte> entry = default;

        foreach (var symb in symbols)
        {
            entry = SearchEntryInFolder(parentEntry, symb, symb.Length);

            if (entry.IsEmpty)
                return null;

            byte indexBlockBits = VolumeCrypto.SBOX_DECRYPT[parentEntry[0]];
            if ((indexBlockBits & 1) == 0) // Is an entry block?
            {
                entryData = SeekEntryPtrToEntryInfo(entry);

                byte entryBits = VolumeCrypto.SBOX_DECRYPT[entry[0]];
                if ((entryBits & 1) == 0) // File Flag, we are done.
                    break;
                else // Is a folder
                {
                    // Combine sub index major and minor (6 bits, + 8 bits)
                    int nextEntryIndex = (VolumeCrypto.SBOX_DECRYPT[entry[0]] >> 2) << 8 | VolumeCrypto.SBOX_DECRYPT[entryData[0]];
                    parentEntry = GetFolderPtr(nextEntryIndex, 0);
                }
            }
            else // Indexing block navigation
            {
                entry = SeekEntryPtrToEntryInfo(entry);
                int indexEntryIndex = VolumeCrypto.SBOX_DECRYPT[entry[0]] | VolumeCrypto.SBOX_DECRYPT[entry[1]] << 8;

                parentEntry = GetFolderPtr(indexEntryIndex, 0);
            }
        }

        /* TODO
        byte bits = VolumeCrypto.SBOX_DECRYPT[entry[0]];
        if ((bits & 1) == 0) // File
        {
            int fileOffset = GetVarInt(entryData, out entryData) * 0x40;
            int compressedSize = GetVarInt(entryData, out entryData);
        }

        int unCompressedSize = GetVarInt(entryData, out entryData);
        ;*/
        return null;
    }

    // 8b131c4
    private Span<byte> GetFolderPtr(int folderIndex, int idk)
    {
        int current = FolderOffsets[folderIndex];
        int next = FolderOffsets[folderIndex + 1];
        int size = (next - current) * 0x40;

        byte[] buf = new byte[size];
        _fs.Position = ToCActualOffset + (current * 0x40);
        _fs.ReadExactly(buf);
        return buf;

    }

    // 8b12ab8
    public static Span<byte> SearchEntryInFolder(Span<byte> folderPtr, ReadOnlySpan<char> targetStr, int targetStrLen)
    {
        short dirBits = (short)(VolumeCrypto.SBOX_DECRYPT[folderPtr[0]] | VolumeCrypto.SBOX_DECRYPT[folderPtr[1]] << 8);
        int entryCount = (dirBits >> 1) & 0x7FF; // 11 bits

        if (entryCount == 0)
            return null;

        int min = 0;
        int max = entryCount;
        int mid;
        do
        {
            mid = (min + max) / 2;
            Span<byte> whole_entry = GetNodeEntry(folderPtr, mid); // We don't need the first byte for now
            Span<byte> entry = whole_entry[1..];

            int strLen = GetVarInt(entry, out _);

            int minLen = targetStrLen;
            if (targetStrLen >= strLen)
                minLen = strLen;

            var strEntry = entry[1..];
#if DEBUG
            byte[] tmpStr = new byte[strLen];
            strEntry.Slice(0, strLen).CopyTo(tmpStr);

            VolumeCrypto.DecryptHeaderPart(tmpStr, tmpStr, strLen);
            string inputString = Encoding.ASCII.GetString(tmpStr);
            Console.WriteLine($"Comparing {inputString} ({mid}) <-> {targetStr.ToString()}");
#endif

            int diff = 0;

            if (minLen > 0)
            {
                int i;
                for (i = 0; i < minLen; i++)
                {
                    byte currentChar = VolumeCrypto.SBOX_DECRYPT[strEntry[0]];
                    byte inputChar = (byte)targetStr[i];
                    diff = inputChar - currentChar;

                    if (diff != 0) // Not a match
                        break;

                    strEntry = strEntry[1..];
                }

                if (diff == 0 && i == minLen)
                    diff = targetStrLen - strLen;
            }
            else
                diff = targetStrLen - strLen;

            if (diff < 0)
                max = mid;
            else if (diff > 0)
                min = mid + 1;
            else
                return whole_entry; // Good

            if (min >= max)
            {
                if (min == 0)
                    return [];

                if ((dirBits & 1) != 0) // Is Index Block
                    GetNodeEntry(folderPtr, min - 1);
            }

        } while (min < max);

        return null;
    }

    // 8b12a1c
    private static Span<byte> GetNodeEntry(Span<byte> folderPtr, int entryIndex)
    {
        if (entryIndex <= 0)
        {
            // Return first
            int s = (int)(VolumeCrypto.SBOX_DECRYPT[folderPtr[0]] | VolumeCrypto.SBOX_DECRYPT[folderPtr[1]] << 8);
            return folderPtr.Slice((12 * ((s >> 1) & 0x7FF) + 7) >> 3);
        }
        else
        {
            // Get short
            ushort comb = (ushort)(VolumeCrypto.SBOX_DECRYPT[folderPtr[(entryIndex * 0x0C >> 3) + 1]] << 8 | VolumeCrypto.SBOX_DECRYPT[folderPtr[(entryIndex * 0x0C >> 3)]]);

            // 12 bits
            int entryOffset = comb >> ((entryIndex & 0x1) << 0x2) & 0b_1111_11111111;
            return folderPtr[entryOffset..];
        }
    }

    // 8b11c4c
    private static int GetVarInt(Span<byte> inPtr, out Span<byte> outPtr)
    {
        int lSize = 0;
        int lTemp = 0;

        const int _7bits = 0b_111_1111;
        while (true)
        {
            lTemp += VolumeCrypto.SBOX_DECRYPT[inPtr[lSize]] & _7bits;
            if (VolumeCrypto.SBOX_DECRYPT[inPtr[lSize++]] > _7bits)
                lTemp <<= 7;
            else
                break;
        }

        outPtr = inPtr[lSize..];
        return lTemp;
    }

    // 8b11f60
    private static Span<byte> SeekEntryPtrToEntryInfo(Span<byte> inPtr)
    {
        Span<byte> tmp = inPtr[1..]; // Skip entry first byte bits
        int len = GetVarInt(tmp, out Span<byte> advancedPtr);
        return advancedPtr.Slice(len);
    }

    public void UnpackAll(string outputDir)
    {
        _fs.Position = ToCActualOffset;

        Span<byte> toc = new byte[ToCLength];
        _fs.ReadExactly(toc);
        VolumeCrypto.DecryptHeaderPart(toc, toc, toc.Length);

        BitStream bs = new BitStream(BitStreamMode.Read, toc, BitStreamSignificantBitOrder.MSB);

        bs.Position = FolderOffsets[0] * 0x40;

        var root = new VolumeEntry();
        RegisterFolder(ref bs, root, "");

        Directory.CreateDirectory(outputDir);
        _files = _files.OrderBy(e => e.FileOffset).ToList();

        using (var sw = new StreamWriter(Path.Combine(outputDir, "files.txt")))
        {
            sw.WriteLine("Generated with GTPSPVolTools by Nenkai");
            sw.WriteLine($"Files: {_files.Count}");
            sw.WriteLine();

            foreach (var file in _files)
                sw.WriteLine(file);
        }

        Console.WriteLine($"Starting to extract {_files.Count} files.");
        Console.WriteLine();

        for (int i = 0; i < _files.Count; i++)
        {
            VolumeEntry file = _files[i];
            if ((i % 10) == 0 || i >= _files.Count - 10)
            {
                int percent = (int)((double)i / _files.Count * 100);
                Console.Write($"\r[{percent}%] Unpacking {file.FullPath}...                         ");
            }

            UnpackFile(outputDir, file);
        }

        Console.WriteLine();
        Console.WriteLine("Done unpacking.");
    }

    private void RegisterFolder(ref BitStream bs, VolumeEntry parent, string parentPath)
    {
        int basePos = bs.Position;

        bool isIndexBlock = bs.ReadBoolBit();
        var entryCount = (int)bs.ReadBits(11);

        List<int> entryOffsets = [];
        for (int i = 0; i < entryCount - 1; i++)
            entryOffsets.Add((int)bs.ReadBits(12));

        bs.AlignToNextByte();

        if (isIndexBlock)
        {
            var indices = new List<IndexEntry>();
            for (int i = 0; i < entryCount; i++)
            {
                IndexEntry entry = new IndexEntry();
                entry.Read(ref bs);
                indices.Add(entry);
            }

            foreach (var index in indices)
            {
                bs.Position = FolderOffsets[index.SubPageIndex] * 0x40;
                RegisterFolder(ref bs, parent, parent.FullPath);
            }

            // Add the index terminator aswell
            if (indices[^1].SubPageIndex + 1 < indices.Count)
            {
                bs.Position = FolderOffsets[indices[^1].SubPageIndex + 1] * 0x40;
                RegisterFolder(ref bs, parent, parent.FullPath);
            }
        }
        else
        {
            var currentEntries = new List<VolumeEntry>(entryCount - 1);
            for (int i = 0; i < entryCount; i++)
            {
                VolumeEntry entry = new VolumeEntry();
                entry.Read(ref bs);

                if (!string.IsNullOrEmpty(parentPath))
                    entry.FullPath = $"{parentPath}/{entry.Name}";
                else
                    entry.FullPath = entry.Name;
                parent.Child.Add(entry);

                currentEntries.Add(entry);
            }

            foreach (var entry in currentEntries)
            {
                if (entry.Type == VolumeEntry.EntryType.Directory)
                {
                    bs.Position = FolderOffsets[entry.SubPageIndex] * 0x40;
                    RegisterFolder(ref bs, entry, entry.FullPath);
                }
                else
                {
                    _files.Add(entry);
                }
            }
        }
    }

    private void UnpackFile(string outputDir, VolumeEntry entry)
    {
        long fileOffset = (DataActualOffset + entry.FileOffset);
        _fs.Position = fileOffset;

        byte[] data = new byte[entry.CompressedSize];
        _fs.ReadExactly(data);
        VolumeCrypto.DecryptFile(fileOffset, data, data.Length);

        if (entry.Compressed)
        {
            if (!Compression.TryInflateInMemory(data, entry.UncompressedSize, out data))
            {
                Console.WriteLine($"ERROR: Could not uncompress {entry.FullPath}");
                return;
            }
        }

        string outputFilePath = Path.Combine(outputDir, entry.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath));

        File.WriteAllBytes(outputFilePath, data);
    }
}
