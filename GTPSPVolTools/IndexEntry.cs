using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using PDTools.Utils;

namespace GTPSPVolTools;

public class IndexEntry
{
    public string IndexerString { get; set; }
    public ushort SubPageIndex { get; set; }

    public void Read(ref BitStream bs)
    {
        byte subDirIndexMajor = bs.ReadByte();
        IndexerString = bs.ReadVarPrefixStringAlt();

        SubPageIndex = (ushort)((subDirIndexMajor << 8) | bs.ReadByte());
    }

    public override string ToString()
    {
        return $"Indexer: {IndexerString} | FolderIndex: {SubPageIndex}";
    }

    public uint GetSerializedSize()
    {
        uint indexEntrySize = sizeof(byte) // Page Index hi
            + (uint)BitStream.GetSizeOfVariablePrefixString(IndexerString) // Name
            + sizeof(byte); // Page Index lo

        return indexEntrySize;
    }
}
