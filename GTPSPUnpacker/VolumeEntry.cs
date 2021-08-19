using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using PDTools.Utils;

namespace GTPSPUnpacker
{
    class VolumeEntry
    {
        public EntryType Type { get; set; }
        public bool Compressed { get; set; }

        public string Name { get; set; }
        public string FullPath { get; set; }

        public int SubDirIndex { get; set; }
        
        public long FileOffset { get; set; }
        public int CompressedSize { get; set; }
        public int UncompressedSize { get; set; }

        public List<VolumeEntry> Child = new List<VolumeEntry>();

        public void Read(ref BitStream bs, bool isIndexEntry = false)
        {
            Type = (EntryType)bs.ReadBits(1);
            Compressed = bs.ReadBoolBit();

            int subFolderIndexMajor = (int)bs.ReadBits(6);

            Name = bs.ReadVarPrefixString();


            if (Type == EntryType.Directory || isIndexEntry) // File
            {
                int subFolderIndexMinor = bs.ReadByte();
                SubDirIndex = (subFolderIndexMajor << 8) | subFolderIndexMinor;
            }
            else
            {
                FileOffset = (int)bs.ReadVarInt() << 6;

                if (Compressed)
                {
                    CompressedSize = (int)bs.ReadVarInt();
                    UncompressedSize = (int)bs.ReadVarInt();
                }
                else
                {
                    UncompressedSize = (int)bs.ReadVarInt();
                    CompressedSize = UncompressedSize;
                }
            }

        }

        public override string ToString()
        {
            var str = $"{FullPath} | Type: {Type}";
            if (Type == EntryType.File)
            {
                str += $" | Offset: {FileOffset} | ZSize: {CompressedSize} | Size: {UncompressedSize}";
            }
            else
            {
                str += $" | {SubDirIndex} ({Child.Count} files)";
            }
            return str;
        }

        public enum EntryType
        {
            File,
            Directory,
        }
    }
}
