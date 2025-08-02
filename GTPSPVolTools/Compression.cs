using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Buffers;
using System.Buffers.Binary;

using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace GTPSPVolTools;

public class Compression
{
    /// <summary>
    /// Decompresses a file (in memory, unsuited for large files).
    /// </summary>
    /// <param name="data"></param>
    /// <param name="outSize"></param>
    /// <param name="deflatedData"></param>
    /// <returns></returns>
    public unsafe static bool TryInflateInMemory(Span<byte> data, uint outSize, out byte[] deflatedData)
    {
        deflatedData = [];
        if (outSize > uint.MaxValue)
            return false;

        // Inflated is always little
        var sr = new SpanReader(data, Endian.Little);
        uint zlibMagic = sr.ReadUInt32();
        uint sizeComplement = sr.ReadUInt32();

        if ((long)zlibMagic != 0xFFF7EEC5)
            return false;

        if (outSize + sizeComplement != 0)
            return false;

        const int headerSize = 8;
        if (sr.Length <= headerSize) // Header size, if it's under, data is missing
            return false;

        deflatedData = new byte[(int)outSize];
        fixed (byte* pBuffer = &sr.Span.Slice(headerSize)[0]) // Vol Header Size
        {
            using var ums = new UnmanagedMemoryStream(pBuffer, sr.Span.Length - headerSize);
            using var ds = new DeflateStream(ums, CompressionMode.Decompress);
            ds.ReadExactly(deflatedData, 0, (int)outSize);
        }

        return true;
    }

    /// <summary>
    /// Compresses input data (PS2ZIP-like) and encrypts it from one stream to another.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>Length of the compressed data.</returns>
    public static uint PS2ZIPCompressEncrypt(Stream input, Stream output)
    {
        long basePos = output.Position;
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xC5EEF7FFu);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], -(int)input.Length);

        VolumeCrypto.EncryptFile((uint)output.Position, header, 8);
        output.Write(header);

        var buffer = ArrayPool<byte>.Shared.Rent((int)input.Length);
        input.ReadExactly(buffer, 0, (int)input.Length);

        var d = new Deflater(Deflater.DEFAULT_COMPRESSION, true);
        d.SetInput(buffer, 0, (int)input.Length);
        d.Finish();

        int count = d.Deflate(buffer);
        VolumeCrypto.EncryptFile((uint)output.Position, buffer, count);
        output.Write(buffer, 0, count);

        ArrayPool<byte>.Shared.Return(buffer);

        return (uint)(output.Position - basePos);
    }

    /// <summary>
    /// Encrypts a file stream to an output stream.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>Length of the compressed data.</returns>
    public static void Encrypt(Stream input, Stream output)
    {
        const int bufferSize = 0x20000;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        uint rem = (uint)input.Length;

        while (rem > 0)
        {
            uint size = Math.Min(rem, bufferSize);
            input.ReadExactly(buffer, 0, (int)size);
            VolumeCrypto.EncryptFile((uint)output.Position, buffer, buffer.Length);
            output.Write(buffer, 0, (int)size);

            rem -= size;
        }

        ArrayPool<byte>.Shared.Return(buffer);
    }
}
