﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Toolbox.Core.IO;

namespace Kontract.Compression
{
    public class LZ40
    {
        public static byte[] Decompress(Stream instream, long decompressedLength)
        {
            using (FileReader br = new FileReader(instream, true))
            {
                byte[] result = new byte[decompressedLength];
                int dstoffset = 0;

                while (true)
                {
                    byte header = (byte)-br.ReadByte();
                    for (int i = 0; i < 8; i++)
                    {
                        if (dstoffset >= 0x1105)
                            ;

                        if ((header & 0x80) == 0) result[dstoffset++] = br.ReadByte();
                        else
                        {
                            byte a = br.ReadByte();
                            byte b = br.ReadByte();

                            int offset = (b << 4) | ((a & 0xF0) >> 4);
                            int length2 = a & 0xF;

                            if ((a & 0xF) == 0)
                            {
                                byte c = br.ReadByte();
                                length2 = c + 0x10;
                            }
                            else if ((a & 0xF) == 1)
                            {
                                byte c = br.ReadByte();
                                byte d = br.ReadByte();
                                length2 = ((d << 8) | c) + 0x110;
                            }

                            for (int j = 0; j < length2; j++)
                            {
                                result[dstoffset] = result[dstoffset - offset];
                                dstoffset++;
                            }
                        }

                        if (dstoffset >= decompressedLength) return result;
                        header <<= 1;
                    }
                }
            }
        }

        public unsafe static byte[] Compress(Stream instream)
        {
            // make sure the decompressed size fits in 3 bytes.
            // There should be room for four bytes, however I'm not 100% sure if that can be used
            // in every game, as it may not be a built-in function.
            long inLength = instream.Length;
            Stream outstream = new MemoryStream();

            // save the input data in an array to prevent having to go back and forth in a file
            byte[] indata = new byte[inLength];
            int numReadBytes = instream.Read(indata, 0, (int)inLength);
            if (numReadBytes != inLength)
                throw new Exception("Stream too short!");

            int compressedLength = 0;

            fixed (byte* instart = &indata[0])
            {
                // we do need to buffer the output, as the first byte indicates which blocks are compressed.
                // this version does not use a look-ahead, so we do not need to buffer more than 8 blocks at a time.
                // (a block is at most 4 bytes long)
                byte[] outbuffer = new byte[8 * 4 + 1];
                outbuffer[0] = 0;
                int bufferlength = 1, bufferedBlocks = 0;
                int readBytes = 0;
                while (readBytes < inLength)
                {
                    #region If 8 blocks are bufferd, write them and reset the buffer
                    // we can only buffer 8 blocks at a time.
                    if (bufferedBlocks == 8)
                    {
                        outbuffer[0] = (byte)-outbuffer[0];
                        outstream.Write(outbuffer, 0, bufferlength);
                        compressedLength += bufferlength;
                        // reset the buffer
                        outbuffer = new byte[8 * 4 + 1];
                        outbuffer[0] = 0;
                        bufferlength = 1;
                        bufferedBlocks = 0;
                    }
                    #endregion

                    // determine if we're dealing with a compressed or raw block.
                    // it is a compressed block when the next 3 or more bytes can be copied from
                    // somewhere in the set of already compressed bytes.
                    int disp;
                    int oldLength = Math.Min(readBytes, 0xFFF);
                    if (readBytes >= 0x1105)
                        ;
                    int length = GetOccurrenceLength(
                        instart + readBytes,
                        (int)Math.Min(inLength - readBytes, 0x1010F),
                        instart + readBytes - oldLength,
                        oldLength,
                        out disp);

                    // length not 3 or more? next byte is raw data
                    if (length < 3)
                    {
                        outbuffer[bufferlength++] = *(instart + (readBytes++));
                    }
                    else
                    {
                        // 3 or more bytes can be copied? next (length) bytes will be compressed into 2 bytes
                        readBytes += length;

                        // mark the next block as compressed
                        outbuffer[0] |= (byte)(1 << (7 - bufferedBlocks));

                        // the last 1.5 bytes are always the disp
                        outbuffer[bufferlength] = (byte)((disp & 0x0F) << 4);
                        outbuffer[bufferlength + 1] = (byte)((disp >> 4) & 0xFF);

                        if (length > 0x10F)
                        {
                            outbuffer[bufferlength] |= 0x01;
                            bufferlength++;
                            bufferlength++;
                            outbuffer[bufferlength] = (byte)((length - 0x110) & 0xFF);
                            bufferlength++;
                            outbuffer[bufferlength] = (byte)(((length - 0x110) >> 8) & 0xFF);
                        }
                        else if (length > 0xF)
                        {
                            outbuffer[bufferlength] |= 0x00;
                            bufferlength++;
                            bufferlength++;
                            outbuffer[bufferlength] = (byte)((length - 0x10) & 0xFF);
                        }
                        else
                        {
                            outbuffer[bufferlength] |= (byte)(length & 0x0F);
                            bufferlength++;
                        }
                        bufferlength++;
                    }
                    bufferedBlocks++;
                }

                // copy the remaining blocks to the output
                if (bufferedBlocks > 0)
                {
                    outbuffer[0] = (byte)-outbuffer[0];
                    outstream.Write(outbuffer, 0, bufferlength);
                    compressedLength += bufferlength;
                    /*/ make the compressed file 4-byte aligned.
                    while ((compressedLength % 4) != 0)
                    {
                        outstream.WriteByte(0);
                        compressedLength++;
                    }/**/
                }
            }

            outstream.Position = 0;
            return new FileReader(outstream).ReadBytes((int)outstream.Length);
        }

        public static unsafe int GetOccurrenceLength(byte* newPtr, int newLength, byte* oldPtr, int oldLength, out int disp, int minDisp = 1)
        {
            disp = 0;
            if (newLength == 0)
                return 0;
            int maxLength = 0;
            // try every possible 'disp' value (disp = oldLength - i)
            for (int i = 0; i < oldLength - minDisp; i++)
            {
                // work from the start of the old data to the end, to mimic the original implementation's behaviour
                // (and going from start to end or from end to start does not influence the compression ratio anyway)
                byte* currentOldStart = oldPtr + i;
                int currentLength = 0;
                // determine the length we can copy if we go back (oldLength - i) bytes
                // always check the next 'newLength' bytes, and not just the available 'old' bytes,
                // as the copied data can also originate from what we're currently trying to compress.
                for (int j = 0; j < newLength; j++)
                {
                    // stop when the bytes are no longer the same
                    if (*(currentOldStart + j) != *(newPtr + j))
                        break;
                    currentLength++;
                }

                // update the optimal value
                if (currentLength > maxLength)
                {
                    maxLength = currentLength;
                    disp = oldLength - i;

                    // if we cannot do better anyway, stop trying.
                    if (maxLength == newLength)
                        break;
                }
            }

            return maxLength;
        }
    }
}