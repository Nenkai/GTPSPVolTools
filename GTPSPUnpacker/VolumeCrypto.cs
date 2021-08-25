﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Runtime.InteropServices;

namespace GTPSPUnpacker
{
    public class VolumeCrypto
    {
        // 48e580
        public static byte[] SBOX_DECRYPT = new byte[]
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

        public static byte[] SBOX_ENCRYPT = new byte[SBOX_DECRYPT.Length];

        static VolumeCrypto()
        {
            // Generate encryption sbox based on decryption
            for (int i = 0; i < SBOX_DECRYPT.Length; i++)
                SBOX_ENCRYPT[SBOX_DECRYPT[i]] = (byte)i;
        }

        // 30dc10
        public static void DecryptHeaderPart(Span<byte> buffer, Span<byte> outBuffer, int size)
        {
            for (int i = 0; i < size; i++)
                outBuffer[i] = SBOX_DECRYPT[buffer[i]];
        }

        public static void EncryptHeaderPart(Span<byte> buffer, Span<byte> outBuffer, int size)
        {
            for (int i = 0; i < size; i++)
                outBuffer[i] = SBOX_ENCRYPT[buffer[i]];
        }

        // 30f7ac
        public static void DecryptFile(uint fileOffset, Span<byte> data, int nSize)
        {
            byte dOffset = (byte)fileOffset;
            Span<byte> currentBuffer = data;

            // Decrypt to align offset to nearest 0x40
            int sizeToRead = nSize;
            if ((fileOffset % 0x40) != 0)
            {
                int size = (int)(0x40 - (fileOffset % 0x40));
                if (nSize < size)
                    sizeToRead = 0;
                else
                {
                    sizeToRead = nSize - size;
                    nSize = size;
                }

                for (int i = 0; i < nSize; i++)
                    data[i] ^= SBOX_DECRYPT[dOffset++];

                dOffset &= 0xFF;
                data = data[nSize..];

                if (sizeToRead == 0)
                    return;
            }


            if (sizeToRead != 0)
            {
                var sboxInts = MemoryMarshal.Cast<byte, uint>(SBOX_DECRYPT);
                var bufInts = MemoryMarshal.Cast<byte, uint>(data);

                int dOffset4;
                while (sizeToRead >= 0x40)
                {
                    dOffset4 = dOffset / sizeof(int);

                    // Note to self: If this ever needs to be faster just use SSE or something
                    bufInts[0] ^= sboxInts[dOffset4];
                    bufInts[1] ^= sboxInts[dOffset4 + 1];
                    bufInts[2] ^= sboxInts[dOffset4 + 2];
                    bufInts[3] ^= sboxInts[dOffset4 + 3];
                    bufInts[4] ^= sboxInts[dOffset4 + 4];
                    bufInts[5] ^= sboxInts[dOffset4 + 5];
                    bufInts[6] ^= sboxInts[dOffset4 + 6];
                    bufInts[7] ^= sboxInts[dOffset4 + 7];
                    bufInts[8] ^= sboxInts[dOffset4 + 8];
                    bufInts[9] ^= sboxInts[dOffset4 + 9];
                    bufInts[10] ^= sboxInts[dOffset4 + 10];
                    bufInts[11] ^= sboxInts[dOffset4 + 11];
                    bufInts[12] ^= sboxInts[dOffset4 + 12];
                    bufInts[13] ^= sboxInts[dOffset4 + 13];
                    bufInts[14] ^= sboxInts[dOffset4 + 14];
                    bufInts[15] ^= sboxInts[dOffset4 + 15];

                    dOffset += 0x40;
                    sizeToRead -= 0x40;

                    bufInts = bufInts[16..];
                    data = data[0x40..];
                }
            }

            // Finish up remaining bytes
            if (sizeToRead > 0)
            {
                for (int i = 0; i < sizeToRead; i++)
                    data[i] ^= SBOX_DECRYPT[dOffset++];
            }
        }

        public static void EncryptFile(uint fileOffset, Span<byte> data, int nSize)
            => DecryptFile(fileOffset, data, nSize);
    }
}
