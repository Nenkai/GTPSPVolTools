using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO;
using Syroot.BinaryData.Memory;

using PDTools.Utils;

namespace GTPSPUnpacker
{
    public class Volume
    {
        private const int MainHeaderBlockSize = 1;
        public const int BlockSize = 0x800;

        public const int VolumeMagic = 0x71D319F3;

        public long Date { get; set; }
        public int ToCBlockOffset { get; set; }
        public int FileDataOffset { get; set; }
        public int FolderCount { get; set; }
        public int ToCLength { get; set; }
        public int UnkSize { get; set; }

        public long ToCActualOffset { get; set; }

        public List<ushort> FolderOffsets { get; set; }

        private List<VolumeEntry> _files { get; set; } = new();

        private string _fileName;
        private FileStream _fs;

        public Volume(string fileName)
        {
            _fileName = fileName;
        }

        public bool Init()
        {
            _fs = File.Open(_fileName, FileMode.Open);

            Span<byte> buf = new byte[0x40];
            _fs.Read(buf);

            // 30ed6c
            Volume.Decrypt(buf[0x04..], buf[0x04..], sizeof(uint));
            Volume.Decrypt(buf[0x08..], buf[0x08..], sizeof(long));
            Volume.Decrypt(buf[0x10..], buf[0x10..], sizeof(uint)); // Block Size
            Volume.Decrypt(buf[0x14..], buf[0x14..], sizeof(uint));
            Volume.Decrypt(buf[0x18..], buf[0x18..], sizeof(uint));
            Volume.Decrypt(buf[0x1c..], buf[0x1c..], sizeof(uint));
            Volume.Decrypt(buf[0x20..], buf[0x20..], sizeof(uint));

            SpanReader sr = new SpanReader(buf);
            int Magic = sr.ReadInt32();

            if (Magic != VolumeMagic)
            {
                Console.WriteLine("Volume: Magic did not match. Not a volume file.");
                return false;
            }

            Date = sr.ReadInt64();
            int unk3 = sr.ReadInt32();
            ToCBlockOffset = sr.ReadInt32();
            FileDataOffset = sr.ReadInt32();
            FolderCount = sr.ReadInt32();
            ToCLength = sr.ReadInt32();
            UnkSize = sr.ReadInt32(); // + 7 >> 3

            ToCActualOffset = (MainHeaderBlockSize + ToCBlockOffset) * BlockSize;
            _fs.Position = ToCActualOffset;

            //Console.WriteLine($"Volume: Created - {Date}");
            Console.WriteLine($"Volume: {FolderCount} Total Folders");
#if DEBUG
            File.WriteAllBytes("main_header.bin", buf.ToArray());

            Span<byte> tocBuf = new byte[ToCLength];
            _fs.Read(tocBuf);
            Volume.Decrypt(tocBuf, tocBuf, ToCLength);
            File.WriteAllBytes("toc.bin", tocBuf.ToArray());

            _fs.Position = 0;
            Span<byte> wholeThing = new byte[ToCActualOffset + ToCLength];
            _fs.Read(wholeThing);
            Volume.Decrypt(wholeThing, wholeThing, (int)(ToCActualOffset + ToCLength));
            File.WriteAllBytes("whole_thing.bin", wholeThing.ToArray());
#endif

            _fs.Position = ToCActualOffset;
            Span<byte> folderCountBuf = new byte[(FolderCount * sizeof(ushort)) + 2];
            _fs.Read(folderCountBuf);
            Volume.Decrypt(folderCountBuf, folderCountBuf, folderCountBuf.Length);

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
        // 30f568
        private object Find(string path)
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

                byte indexBlockBits = DECRYPT_SBOX[parentEntry[0]];
                if ((indexBlockBits & 1) == 0) // Is an entry block?
                {
                    entryData = SeekEntryPtrToEntryInfo(entry);

                    byte entryBits = DECRYPT_SBOX[entry[0]];
                    if ((entryBits & 1) == 0) // File Flag, we are done.
                        break;
                    else // Is a folder
                    {
                        // Combine sub index major and minor (6 bits, + 8 bits)
                        int nextEntryIndex = (DECRYPT_SBOX[entry[0]] >> 2) << 8 | DECRYPT_SBOX[entryData[0]];
                        parentEntry = GetFolderPtr(nextEntryIndex, 0);
                    }
                }
                else // Indexing block navigation
                {
                    entry = SeekEntryPtrToEntryInfo(entry);
                    int indexEntryIndex = DECRYPT_SBOX[entry[0]] | DECRYPT_SBOX[entry[1]] << 8;

                    parentEntry = GetFolderPtr(indexEntryIndex, 0);
                }
            }

            byte bits = DECRYPT_SBOX[entry[0]];
            if ((bits & 1) == 0) // File
            {
                int fileOffset = GetVarInt(entryData, out entryData) * 0x40;
                int compressedSize = GetVarInt(entryData, out entryData);
            }

            int unCompressedSize = GetVarInt(entryData, out entryData);
            ;
            return null;
        }

        // 30f1c4
        private Span<byte> GetFolderPtr(int folderIndex, int idk)
        {
            int current = FolderOffsets[folderIndex];
            int next = FolderOffsets[folderIndex + 1];
            int size = (next - current) * 0x40;

            byte[] buf = new byte[size];
            _fs.Position = ToCActualOffset + (current * 0x40);
            _fs.Read(buf);
            return buf;

        }

        // 30EAB8
        public Span<byte> SearchEntryInFolder(Span<byte> folderPtr, ReadOnlySpan<char> targetStr, int targetStrLen)
        {
            short dirBits = (short)(DECRYPT_SBOX[folderPtr[0]] | DECRYPT_SBOX[folderPtr[1]] << 8);
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
                Span<byte> entry = whole_entry.Slice(1);

                int strLen = GetVarInt(entry, out _);

                int minLen = targetStrLen;
                if (targetStrLen >= strLen)
                    minLen = strLen;

                var strEntry = entry[1..];
#if DEBUG
                byte[] tmpStr = new byte[strLen];
                strEntry.Slice(0, strLen).CopyTo(tmpStr);

                Volume.Decrypt(tmpStr, tmpStr, strLen);
                string inputString = Encoding.ASCII.GetString(tmpStr);
                Console.WriteLine($"Comparing {inputString} ({mid}) <-> {targetStr.ToString()}");
#endif

                int diff = 0;

                if (minLen > 0)
                {
                    int i;
                    for (i = 0; i < minLen; i++)
                    {
                        byte currentChar = DECRYPT_SBOX[strEntry[0]];
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
                        return Span<byte>.Empty;

                    if ((dirBits & 1) != 0) // Is Index Block
                        GetNodeEntry(folderPtr, min - 1);
                }

            } while (min < max);

            return null;
        }

        // 30ea1c
        private Span<byte> GetNodeEntry(Span<byte> folderPtr, int entryIndex)
        {
            if (entryIndex <= 0)
            {
                // Return first

                int s = (int)(DECRYPT_SBOX[folderPtr[0]] | DECRYPT_SBOX[folderPtr[1]] << 8);
                return folderPtr.Slice((12 * ((s >> 1) & 0x7FF) + 7) >> 3);
            }
            else
            {
                // Get short
                ushort comb = (ushort)(DECRYPT_SBOX[folderPtr[(entryIndex * 0x0C >> 3) + 1]] << 8 | DECRYPT_SBOX[folderPtr[(entryIndex * 0x0C >> 3)]]);

                // 12 bits
                int entryOffset = comb >> ((entryIndex & 0x1) << 0x2) & 0b_1111_11111111;
                return folderPtr[entryOffset..];
            }
        }

        // 30DC4C
        private int GetVarInt(Span<byte> inPtr, out Span<byte> outPtr)
        {
            int lSize = 0;
            int lTemp = 0;

            const int _7bits = 0b_111_1111;
            while (true)
            {
                lTemp += DECRYPT_SBOX[inPtr[lSize]] & _7bits;
                if (DECRYPT_SBOX[inPtr[lSize++]] > _7bits)
                    lTemp <<= 7;
                else
                    break;
            }

            outPtr = inPtr[lSize..];
            return lTemp;
        }

        // 30df60
        private Span<byte> SeekEntryPtrToEntryInfo(Span<byte> inPtr)
        {
            Span<byte> tmp = inPtr[1..]; // Skip entry first byte bits
            int len = GetVarInt(tmp, out Span<byte> advancedPtr);
            return advancedPtr.Slice(len);
        }

        public void UnpackAll(string outputDir)
        {
            _fs.Position = ToCActualOffset;

            Span<byte> toc = new byte[ToCLength];
            _fs.Read(toc);
            Volume.Decrypt(toc, toc, toc.Length);

            BitStream bs = new BitStream(BitStreamMode.Read, toc, BitStreamSignificantBitOrder.MSB);

            bs.Position = FolderOffsets[0] * 0x40;

            var root = new VolumeEntry();
            RegisterFolder(ref bs, root, "");

            Directory.CreateDirectory(outputDir);

            using (var sw = new StreamWriter(Path.Combine(outputDir, "files.txt"))) 
            {
                sw.WriteLine("Generated with GTPSPUnpacker by Nenkai#9075");
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

            List<int> entryOffsets = new List<int>();
            for (int i = 0; i < entryCount - 1; i++)
                entryOffsets.Add((int)bs.ReadBits(12));

            bs.AlignToNextByte();

            if (entryCount == 1)
                entryCount++;

            if (isIndexBlock)
            {
                var indices = new List<IndexEntry>();
                for (int i = 0; i < entryCount - 1; i++)
                {
                    IndexEntry entry = new IndexEntry();
                    entry.Read(ref bs);
                    indices.Add(entry);
                }

                foreach (var index in indices)
                {
                    bs.Position = FolderOffsets[index.SubDirIndex] * 0x40;
                    RegisterFolder(ref bs, parent, parent.FullPath);
                }

                // Add the index terminator aswell
                bs.Position = FolderOffsets[indices[^1].SubDirIndex + 1] * 0x40;
                RegisterFolder(ref bs, parent, parent.FullPath);
            }
            else
            {
                var currentEntries = new List<VolumeEntry>(entryCount - 1);
                for (int i = 0; i < entryCount - 1; i++)
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
                        bs.Position = FolderOffsets[entry.SubDirIndex] * 0x40;
                        RegisterFolder(ref bs, entry, entry.FullPath);
                    }
                    else
                        _files.Add(entry);
                }
            }
        }

        private void UnpackFile(string outputDir, VolumeEntry entry)
        {
            long fileOffset = (long)((1 + this.ToCBlockOffset + this.FileDataOffset) * BlockSize) + entry.FileOffset;
            _fs.Position = fileOffset;

            byte[] data = new byte[entry.CompressedSize];
            _fs.Read(data);
            Volume.DecryptFile(data, (int)(fileOffset % 256));

            if (entry.Compressed)
                Utils.TryInflateInMemory(data, (ulong)entry.UncompressedSize, out data);

            string outputFilePath = Path.Combine(outputDir, entry.FullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath));

            File.WriteAllBytes(outputFilePath, data);
        }

        public static void DecryptFile(Span<byte> data, int index = 0)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (index < DECRYPT_SBOX.Length)
                {
                    index++;
                }
                else
                {
                    index = 1;
                }
                data[i] = (byte)(data[i] ^ DECRYPT_SBOX[index - 1]);
            }
        }
        public static void Decrypt(Span<byte> buffer, Span<byte> outBuffer, int size)
        {
            for (int i = 0; i < size; i++)
                outBuffer[i] = DECRYPT_SBOX[buffer[i]];
        }

        private ReadOnlySpan<char> TruncateToNextDirChar(ReadOnlySpan<char> path)
        {
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/')
                    return path.Slice(i);
            }

            return path.Slice(path.Length - 1);
        }

        private ReadOnlySpan<char> TruncateToNextActualDir(ReadOnlySpan<char> path)
        {
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/')
                    return path.Slice(i + 1);
            }

            return path.Slice(path.Length - 1);
        }

        private static byte[] DECRYPT_SBOX = new byte[]
        {
             0x0,  0x6, 0x86, 0x57, 0x97, 0x69, 0x6C, 0xB5, 0xBD, 0xD6, 0xBE, 0x34, 0xC2, 0x35, 0xCE, 0xFA,
             0xE, 0x7E, 0x2F, 0xD0, 0x9A, 0x8E, 0xB4, 0x82, 0x25, 0x58, 0x1F, 0x6D, 0x90, 0xF5, 0x8B, 0xA5,
            0xE5, 0x96, 0x56, 0xFF, 0x3B, 0x2B, 0x6B, 0xAE, 0x98, 0x32, 0x2D, 0x60, 0x45, 0xFE, 0x81, 0xA7,
            0xEC, 0x1B, 0xDA, 0xC9, 0xDC, 0x3C, 0x52, 0xF9, 0x7B,  0x4, 0x63, 0xC6, 0xDB, 0xCA, 0x1C, 0x3D,
            0xD1, 0x7A, 0xFD, 0x6F, 0xF1, 0xCD, 0x9C, 0x4D, 0x78, 0x74,  0xD, 0x40, 0x51, 0x8D, 0x64, 0x5C,
            0xCB, 0x49, 0xF8, 0x39, 0x24, 0x30, 0x3A, 0xE2, 0x22, 0x61, 0xA4, 0x89,  0x9, 0x65, 0xAD, 0x1D,
            0xEF, 0x4E, 0xC8, 0xB8, 0x10, 0xA2, 0xDF,  0xF, 0xFB, 0x66, 0x54, 0xA6, 0x1E, 0x11, 0x73, 0x62,
            0x13, 0x21, 0x46, 0xB9, 0x33, 0x9D, 0x88, 0xB2, 0xE3, 0x37,  0xC, 0x4F, 0x84, 0x3E, 0xF0, 0x16,
            0x70, 0x36, 0xDE, 0x8A, 0x1A, 0xEE, 0x28, 0xBC, 0x9F,  0x5, 0x80, 0x67, 0x4A, 0x7C, 0xE0, 0x53,
            0x2E, 0xE7, 0xA3, 0x6A, 0xFC,  0x3, 0x41, 0x6E, 0xD8, 0x14, 0x38, 0xBB, 0xF6, 0xEB, 0x19, 0xAC,
            0x48, 0xD4, 0x27, 0x44, 0xC4,  0x8, 0x95,  0x7, 0x43, 0xD5, 0x18, 0x26, 0xF4, 0x20, 0x75, 0x77,
            0xC0, 0xA1, 0x99,  0xB, 0xC3, 0x85, 0xE1, 0xBA, 0x4C, 0xB3, 0x9B, 0x47, 0x23, 0xAF, 0x8C, 0x72,
            0x68, 0xA8, 0xCC, 0xC7, 0xAB, 0x5F, 0x5D, 0x93, 0x3F, 0x5E, 0x87, 0x9E, 0xCF, 0xD7, 0xB0, 0x4B,
            0x76, 0xD9, 0x71, 0x31, 0xB6, 0x7D, 0x50,  0xA, 0x5B, 0xE8, 0x83, 0x2A, 0xB7, 0x7F, 0xDD, 0x59,
            0xC5, 0x79, 0x5A, 0xF7, 0xA9, 0xE9, 0xD2, 0xED, 0xE6, 0xBF, 0xF3, 0xB1, 0x29,  0x1, 0x12, 0xAA,
            0x91, 0x92, 0xF2, 0x15, 0xE4, 0xD3, 0x17, 0x42,  0x2, 0xA0, 0x8F, 0xC1, 0x2C, 0x94, 0x55, 0xEA,
        };
    }


}
