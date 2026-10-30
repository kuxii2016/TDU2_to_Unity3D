// Portiert aus EdenSavegame (TDU2 Savegame editor) für Unity (C# 9 / .NET Standard 2.1, ohne unsafe).
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EdenSavegame
{
    public sealed class DecryptedSave
    {
        public byte[] Xmbf { get; }
        public byte[] Footer { get; }
        public string FileName { get; }
        public DecryptedSave(byte[] xmbf, byte[] footer, string fileName) { Xmbf = xmbf; Footer = footer; FileName = fileName; }
    }

    /// <summary>
    /// TDU2 encrypted save container: 4096-byte chunk framing + Eden-SHA1 digests,
    /// DES-CBC ciphertext and the authentic Eden Games footer.
    /// </summary>
    public static class SaveCrypto
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        public static byte[] GetProfileCrcBytes(string profileName)
        {
            uint crc = Crc32(NetCompat.Latin1Bytes(profileName)) & 0xFFFF;
            var b = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(b, crc);
            return b;
        }

        public static DecryptedSave Decrypt(byte[] file, string? profileName, string? filenameHint, string containerName = "PLAYERSAVE")
        {
            if (file.Length < 32)
                throw new InvalidDataException("File is too small to be a valid TDU2 save.");

            int fnameLen = file[file.Length - 1];
            int footerLen = 2 + 8 + fnameLen + 2 + 1;
            if (file.Length < footerLen)
                throw new InvalidDataException("Corrupt save footer length.");

            byte[] footer = NetCompat.Sub(file, file.Length - footerLen, file.Length);
            if (footer[0] != (byte)'#' || footer[1] != (byte)'#' ||
                footer[footerLen - 3] != (byte)'#' || footer[footerLen - 2] != (byte)'#')
                throw new InvalidDataException("Invalid save footer magic.");

            string extracted = Encoding.ASCII.GetString(footer, 10, fnameLen);
            string target = filenameHint ?? extracted;

            byte[] profileCrcBytes = profileName != null
                ? GetProfileCrcBytes(profileName)
                : NetCompat.Sub(footer, 2, 10);

            var keyStr = new byte[6 + 2 + containerName.Length + target.Length];
            int p = 0;
            for (int i = 0; i < 6; i++) keyStr[p++] = 0x20;
            keyStr[p++] = profileCrcBytes[0];
            keyStr[p++] = profileCrcBytes[1];
            Encoding.ASCII.GetBytes(containerName, 0, containerName.Length, keyStr, p);
            p += containerName.Length;
            Encoding.ASCII.GetBytes(target, 0, target.Length, keyStr, p);

            int[][] sk = DesCrypto.Kdf(keyStr);

            int ctLen = file.Length - footerLen;
            if (ctLen % 8 != 0)
                throw new InvalidDataException("Ciphertext length is not a multiple of 8.");

            uint ivL = BinaryPrimitives.ReadUInt32BigEndian(profileCrcBytes.AsSpan(0, 4));
            uint ivR = BinaryPrimitives.ReadUInt32BigEndian(profileCrcBytes.AsSpan(4, 4));
            uint prevL = ivL, prevR = ivR;

            var pt = new byte[ctLen];
            for (int blk = 0; blk < ctLen / 8; blk++)
            {
                uint cL = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(blk * 8, 4));
                uint cR = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(blk * 8 + 4, 4));
                (uint dL, uint dR) = DesCrypto.DesDecrypt(cL, cR, sk);
                BinaryPrimitives.WriteUInt32BigEndian(pt.AsSpan(blk * 8, 4), dL ^ prevL);
                BinaryPrimitives.WriteUInt32BigEndian(pt.AsSpan(blk * 8 + 4, 4), dR ^ prevR);
                prevL = cL; prevR = cR;
            }

            uint partialSize = BinaryPrimitives.ReadUInt32LittleEndian(pt.AsSpan(0, 4));
            uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(pt.AsSpan(8, 4));
            if (totalSize > 50_000_000)
                throw new InvalidDataException("Sanity check failed: total size too large.");

            var result = new byte[totalSize];
            int curr = 16;
            long rem = totalSize;
            int outPos = 0;
            int chunkIdx = 0;
            while (rem > 0)
            {
                int cLen = (int)Math.Min(rem, 4096);
                int pLen = (cLen + 7) & ~7;
                if (curr + cLen > pt.Length)
                    throw new InvalidDataException("Truncated chunk payload.");

                Buffer.BlockCopy(pt, curr, result, outPos, cLen);
                curr += pLen;
                byte[] hash = NetCompat.Sub(pt, curr, curr + 20);
                curr += 24;

                byte[] calc = EdenSha1.Compute(result.AsSpan(outPos, cLen).ToArray());
                if (!NetCompat.Same(hash, calc, 20))
                    throw new InvalidDataException($"Checksum mismatch in chunk {chunkIdx}.");

                outPos += cLen;
                rem -= cLen;
                chunkIdx++;
            }

            _ = partialSize;
            return new DecryptedSave(result, footer, target);
        }

        public static byte[] Encrypt(byte[] xmbf, string profileName, string fileName, byte[]? originalFooter = null, string containerName = "PLAYERSAVE")
        {
            byte[] profileCrcBytes = GetProfileCrcBytes(profileName);
            var keyStr = new byte[6 + 2 + containerName.Length + fileName.Length];
            int p = 0;
            for (int i = 0; i < 6; i++) keyStr[p++] = 0x20;
            keyStr[p++] = profileCrcBytes[0];
            keyStr[p++] = profileCrcBytes[1];
            Encoding.ASCII.GetBytes(containerName, 0, containerName.Length, keyStr, p);
            p += containerName.Length;
            Encoding.ASCII.GetBytes(fileName, 0, fileName.Length, keyStr, p);

            int[][] sk = DesCrypto.Kdf(keyStr);

            int total = xmbf.Length;
            int partialSize = total % 4096;
            if (partialSize == 0) partialSize = 4096;

            using var ms = new MemoryStream();
            var hdr = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), (uint)partialSize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), (uint)total);
            ms.Write(hdr, 0, hdr.Length);

            int offset = 0;
            while (offset < total)
            {
                int cLen = Math.Min(4096, total - offset);
                byte[] chunk = NetCompat.Sub(xmbf, offset, offset + cLen);
                offset += cLen;
                int pLen = (cLen + 7) & ~7;

                ms.Write(chunk, 0, chunk.Length);
                for (int i = cLen; i < pLen; i++)
                    ms.WriteByte(0);

                byte[] digest = EdenSha1.Compute(chunk);
                ms.Write(digest, 0, digest.Length);
                ms.Write(new byte[4], 0, 4);
            }

            byte[] payload = ms.ToArray();
            if (payload.Length % 8 != 0)
                throw new InvalidDataException("Payload not 8-byte aligned.");

            uint ivL = BinaryPrimitives.ReadUInt32BigEndian(profileCrcBytes.AsSpan(0, 4));
            uint ivR = BinaryPrimitives.ReadUInt32BigEndian(profileCrcBytes.AsSpan(4, 4));
            uint prevL = ivL, prevR = ivR;

            var ct = new byte[payload.Length];
            for (int blk = 0; blk < payload.Length / 8; blk++)
            {
                uint pL = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(blk * 8, 4));
                uint pR = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(blk * 8 + 4, 4));
                (uint cL, uint cR) = DesCrypto.DesEncrypt(pL ^ prevL, pR ^ prevR, sk);
                BinaryPrimitives.WriteUInt32BigEndian(ct.AsSpan(blk * 8, 4), cL);
                BinaryPrimitives.WriteUInt32BigEndian(ct.AsSpan(blk * 8 + 4, 4), cR);
                prevL = cL; prevR = cR;
            }

            byte[] footer = originalFooter ?? BuildFooter(profileCrcBytes, fileName);

            var output = new byte[ct.Length + footer.Length];
            Buffer.BlockCopy(ct, 0, output, 0, ct.Length);
            Buffer.BlockCopy(footer, 0, output, ct.Length, footer.Length);
            return output;
        }

        public static byte[] BuildFooter(byte[] profileCrcBytes, string fileName)
        {
            byte[] fn = Encoding.ASCII.GetBytes(fileName);
            var footer = new byte[2 + 8 + fn.Length + 2 + 1];
            footer[0] = (byte)'#';
            footer[1] = (byte)'#';
            Buffer.BlockCopy(profileCrcBytes, 0, footer, 2, 8);
            Buffer.BlockCopy(fn, 0, footer, 10, fn.Length);
            footer[10 + fn.Length] = (byte)'#';
            footer[10 + fn.Length + 1] = (byte)'#';
            footer[footer.Length - 1] = (byte)fn.Length;
            return footer;
        }

        /// <summary>
        /// Tests whether a profile name decrypts the header of the given save to a valid XMBF stream.
        /// </summary>
        public static bool MatchesProfile(byte[] file, string filenameHint, string candidate)
        {
            try
            {
                int fnameLen = file[file.Length - 1];
                int footerLen = 2 + 8 + fnameLen + 2 + 1;
                if (file.Length <= footerLen + 24) return false;

                byte[] profileCrcBytes = GetProfileCrcBytes(candidate);
                string fname = filenameHint.ToUpperInvariant();
                var keyStr = new byte[6 + 2 + 10 + fname.Length];
                int p = 0;
                for (int i = 0; i < 6; i++) keyStr[p++] = 0x20;
                keyStr[p++] = profileCrcBytes[0];
                keyStr[p++] = profileCrcBytes[1];
                Encoding.ASCII.GetBytes("PLAYERSAVE", 0, 10, keyStr, p);
                p += 10;
                Encoding.ASCII.GetBytes(fname, 0, fname.Length, keyStr, p);

                int[][] sk = DesCrypto.Kdf(keyStr);
                uint ivL = BinaryPrimitives.ReadUInt32BigEndian(profileCrcBytes.AsSpan(0, 4));
                uint ivR = BinaryPrimitives.ReadUInt32BigEndian(profileCrcBytes.AsSpan(4, 4));

                uint c0L = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(0, 4));
                uint c0R = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4, 4));
                (uint d0L, uint d0R) = DesCrypto.DesDecrypt(c0L, c0R, sk);
                uint p0L = d0L ^ ivL, p0R = d0R ^ ivR;

                uint c1L = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8, 4));
                uint c1R = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(12, 4));
                (uint d1L, uint d1R) = DesCrypto.DesDecrypt(c1L, c1R, sk);
                uint p1L = d1L ^ c0L, p1R = d1R ^ c0R;

                var hdr = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0, 4), p0L);
                BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4, 4), p0R);
                BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8, 4), p1L);
                BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(12, 4), p1R);

                uint partialSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0, 4));
                uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8, 4));
                if (totalSize == 0 || totalSize >= 50_000_000 || partialSize == 0 || partialSize > 4096)
                    return false;

                uint c2L = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(16, 4));
                uint c2R = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(20, 4));
                (uint d2L, uint d2R) = DesCrypto.DesDecrypt(c2L, c2R, sk);
                uint p2L = d2L ^ c1L, p2R = d2R ^ c1R;
                var magic = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(magic.AsSpan(0, 4), p2L);
                BinaryPrimitives.WriteUInt32BigEndian(magic.AsSpan(4, 4), p2R);
                return magic[0] == (byte)'X' && magic[1] == (byte)'M' && magic[2] == (byte)'B' && magic[3] == (byte)'F';
            }
            catch
            {
                return false;
            }
        }
    }
}
