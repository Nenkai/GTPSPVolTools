using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using PDTools.Utils;

namespace GTPSPVolTools;

public class VolumeEntry
{
    // For listing
    public string FullPath { get; set; }

    // For building 
    public int EntryOffset { get; set; }
    public ushort EntriesLocationSegmentIndex;
    public ushort DirDefinitionSegmentIndex;

    // Actual entry data
    public EntryType Type { get; set; }
    public bool Compressed { get; set; }
    public string Name { get; set; }
    public int SubDirIndex { get; set; }
    public uint FileOffset { get; set; }
    public uint CompressedSize { get; set; }
    public uint UncompressedSize { get; set; }

    public List<VolumeEntry> Child = [];

    public void Read(ref BitStream bs)
    {
        Type = (EntryType)bs.ReadBits(1);
        Compressed = bs.ReadBoolBit();

        int subFolderIndexMajor = (int)bs.ReadBits(6);

        Name = bs.ReadVarPrefixStringAlt();


        if (Type == EntryType.Directory) 
        {
            int subFolderIndexMinor = bs.ReadByte();
            SubDirIndex = (subFolderIndexMajor << 8) | subFolderIndexMinor;
        }
        else
        {
            FileOffset = (uint)bs.ReadVarIntAlt() * 0x40;

            if (Compressed)
            {
                CompressedSize = (uint)bs.ReadVarIntAlt();
                UncompressedSize = (uint)bs.ReadVarIntAlt();
            }
            else
            {
                UncompressedSize = (uint)bs.ReadVarIntAlt();
                CompressedSize = UncompressedSize;
            }
        }

    }

    public uint GetSerializedKeySize()
    {
        uint length = 1;
        length += (uint)BitStream.GetSizeOfVariablePrefixString(Name);

        if (Type == EntryType.Directory)
            length += 1;
        else if (Type == EntryType.File)
        {
            length += (uint)BitStream.GetSizeOfVarIntAlt(FileOffset / 0x40);
            if (Compressed)
                length += (uint)BitStream.GetSizeOfVarIntAlt(CompressedSize);
            length += (uint)BitStream.GetSizeOfVarIntAlt(UncompressedSize);
        }

        return length;
    }

    public void Serialize(ref BitStream bs)
    {
        bs.WriteBoolBit(Type == EntryType.Directory);
        bs.WriteBoolBit(Compressed);
        bs.WriteBits(0, 6);

        bs.WriteVarPrefixString(Name);
        if (Type == EntryType.Directory)
        {
            bs.WriteByte(0);
        }
        else
        {
            bs.WriteVarIntAlt(FileOffset / 0x40);
            if (Compressed)
                bs.WriteVarIntAlt(CompressedSize);
            bs.WriteVarIntAlt(UncompressedSize);
        }
    }

    public override string ToString()
    {
        var str = $"{FullPath} ({Name}) | Type: {Type}";
        if (Type == EntryType.File)
            str += $" | Offset: {FileOffset:X8} | Compressed: {Compressed} | ZSize: {CompressedSize:X8} | Size: {UncompressedSize:X8}";
        else
            str += $" | {SubDirIndex} ({Child.Count} files)";

        return str;
    }

    public enum EntryType
    {
        File,
        Directory,
    }
}
