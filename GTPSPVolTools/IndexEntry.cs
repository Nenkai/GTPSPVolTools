using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using PDTools.Utils;

namespace GTPSPVolTools;

class IndexEntry
{
    public string Indexer { get; set; }
    public short SubDirIndex { get; set; }

    public void Read(ref BitStream bs)
    {
        byte subDirIndexMajor = bs.ReadByte();
        Indexer = bs.ReadVarPrefixStringAlt();

        SubDirIndex = (short)((subDirIndexMajor << 8) | bs.ReadByte());
    }

    public override string ToString()
    {
        return $"Indexer: {Indexer} | FolderIndex: {SubDirIndex}";
    }

}
