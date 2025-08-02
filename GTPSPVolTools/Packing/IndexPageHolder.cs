using PDTools.Utils;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace GTPSPVolTools.Packing;

public class IndexPageHolder : PageHolderBase
{
    public const int BTREE_PAGE_SIZE = 0x1000;

    public int EntriesInfoSize = 0;
    public List<IndexEntry> Entries { get; private set; } = [];
    public bool IsEmpty => Entries.Count == 0;

    public IndexPageHolder Parent { get; set; }

    public IndexPageHolder()
    {

    }

    public bool TryAddEntry(IndexEntry entry)
    {
        uint keySize = entry.GetSerializedSize();

        int currentSizeTaken = Utils.MeasureBytesTakenByBits(12 + ((Entries.Count - 1) * 12)); // 12 bits header, 12 bits for each entry offset (except first)
        currentSizeTaken += EntriesInfoSize; // current size of all entries in this page

        if (currentSizeTaken + (keySize + 2) > VolumeBuilder.BTREE_MAX_PAGE_SIZE) // Extra 2 to fit the 12 bits offset as short
            return false; // Entry cannot be written, exit loop to create new page

        Entries.Add(entry);
        EntriesInfoSize += (int)keySize;
        return true;
    }

    public override void Write(ref BitStream stream)
    {
        BitStream entryWriter = new BitStream(BitStreamMode.Write, endian: BitStreamSignificantBitOrder.MSB);
        List<int> entryOffsets = [];

        for (int i = 0; i < Entries.Count; i++)
        {
            if (i != 0)
                entryOffsets.Add(entryWriter.Position);

            entryWriter.WriteByte((byte)(Entries[i].SubPageIndex >> 8)); // Major 8 bits
            entryWriter.WriteVarPrefixStringAlt(Entries[i].IndexerString);
            entryWriter.WriteByte((byte)(Entries[i].SubPageIndex & 0xFF)); // Minor 8 bits
            entryWriter.AlignToNextByte();
        }

        stream.WriteBoolBit(true); // Index Block

        Debug.Assert((ulong)Entries.Count < Utils.GetMaxValueForBitCount(11),
            $"IndexWriter: Index Count was larger that could fit 11 bits ({Entries.Count} < {Utils.GetMaxValueForBitCount(11)})");
        stream.WriteBits((ulong)Entries.Count, 11);

        for (int i = 0; i < entryOffsets.Count; i++)
        {
            uint tocSize = (uint)Utils.MeasureBytesTakenByBits(12 + (entryOffsets.Count * 12));
            Debug.Assert((ulong)(tocSize + entryOffsets[i]) < Utils.GetMaxValueForBitCount(12),
                $"IndexWriter: Index offset was larger that could fit 12 bits ({(ulong)(tocSize + entryOffsets[i])} < {Utils.GetMaxValueForBitCount(12)})");
            stream.WriteBits((ulong)(tocSize + entryOffsets[i]), 12);
        }

        stream.WriteByteData(entryWriter.GetSpan());
        stream.Align(VolumeBuilder.BLOCK_SIZE);
    }
}