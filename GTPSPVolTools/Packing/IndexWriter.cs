using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using PDTools.Utils;

namespace GTPSPVolTools.Packing
{
    public class IndexWriter
    {
        public const int BTREE_SEGMENT_SIZE = 0x1000;

        public int CurrentDataLength = 0;

        public byte SegmentCount = 0;
        private List<VolumeEntry> _indices { get; set; } = new();

        public bool IsEmpty
            => _indices.Count == 0;

        public IndexWriter()
        {

        }

        public void AddIndex(VolumeEntry entry)
        {
            _indices.Add(entry);
        }

        public void Write(ref BitStream stream)
        {
            BitStream entryWriter = new BitStream(BitStreamMode.Write, endian: BitStreamSignificantBitOrder.MSB);
            List<int> entryOffsets = new List<int>();

            for (int i = 0; i < _indices.Count; i++)
            {
                if (i != 0)
                    entryOffsets.Add(entryWriter.Position);

                entryWriter.WriteByte((byte)(_indices[i].EntriesLocationSegmentIndex >> 8)); // Major 8 bits
                entryWriter.WriteVarPrefixString(_indices[i].Name);
                entryWriter.WriteByte((byte)(_indices[i].EntriesLocationSegmentIndex & 0xFF)); // Minor 8 bits
                entryWriter.AlignToNextByte();
            }

            stream.WriteBoolBit(true); // Index Block

            Debug.Assert((ulong)_indices.Count < Utils.GetMaxValueForBitCount(11),
                $"IndexWriter: Index Count was larger that could fit 11 bits ({_indices.Count} < {Utils.GetMaxValueForBitCount(11)})");
            stream.WriteBits((ulong)_indices.Count, 11);

            for (int i = 0; i < entryOffsets.Count; i++)
            {
                uint tocSize = (uint)Utils.MeasureBytesTakenByBits(12 + (entryOffsets.Count * 12));

                Debug.Assert((ulong)(tocSize + entryOffsets[i]) < Utils.GetMaxValueForBitCount(12),
                    $"IndexWriter: Index offset was larger that could fit 12 bits ({(ulong)(tocSize + entryOffsets[i])} < {Utils.GetMaxValueForBitCount(12)})");
                stream.WriteBits((ulong)(tocSize + entryOffsets[i]), 12);
            }

            stream.WriteByteData(entryWriter.GetSpan());
            stream.Align(0x40);
        }

    }
}