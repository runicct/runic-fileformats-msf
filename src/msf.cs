/*
 * MIT License
 * 
 * Copyright (c) 2026 Runic Compiler Toolkit Contributors
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.IO;
using System.Collections.Generic;

namespace Runic.FileFormats
{
    public partial class MSF
    {
        public class Stream : System.IO.Stream
        {
            public override bool CanRead { get { return _stream.CanRead; } }

            public override bool CanSeek { get { return true; } }

            public override bool CanWrite { get { return _stream.CanWrite; } }

            public override long Length { get { return _length; } }

            public override long Position { get { return _position; } set { _position = value; } }

            uint[] _blocks;
            internal uint[] Blocks { get { return _blocks; } }
            System.IO.Stream _stream;
            public System.IO.Stream BaseStream { get { return _stream; } set { _stream = value; } }
            long _baseOffset;
            public long BaseStreamOffset { get { return _baseOffset; } set { _baseOffset = value; } }
            long _length;
            long _position;
            int _currentBlockSize;
            long _currentBlockIndex = -1;
            byte[] _currentBlock;
            byte[] ReadBlock(uint blockIndex)
            {
                lock (this)
                {
                    if (blockIndex >= _blocks.Length) { throw new Exception("Block index out of range."); }
                    if (_currentBlockIndex != blockIndex)
                    {
                        long blockOffset = _baseOffset + (_blocks[blockIndex] * _currentBlock.Length);
                        _stream.Position = blockOffset;
                        if (blockIndex == _blocks.Length - 1)
                        {
                            _currentBlockSize = (int)(_length - ((long)blockIndex * (long)_currentBlock.Length));
                            int bytesRead = _stream.Read(_currentBlock, 0, _currentBlockSize);
                            if (bytesRead != _currentBlockSize) { throw new System.Exception("Failed to read the entire block from the stream."); }
                        }
                        else
                        {
                            _currentBlockSize = _stream.Read(_currentBlock, 0, _currentBlock.Length);
                            if (_currentBlockSize != _currentBlock.Length) { throw new System.Exception("Failed to read the entire block from the stream."); }
                        }
                        _currentBlockIndex = blockIndex;
                    }
                    return _currentBlock;
                }
            }

            internal Stream(uint blockSize, uint[] blocks, long length, System.IO.Stream stream, long baseOffset)
            {
                _length = length;
                _blocks = blocks;
                _stream = stream;
                _baseOffset = baseOffset;
                _currentBlock = new byte[blockSize];
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count <= 0) { return 0; }
                long end = _position + count;
                if (end > _length)  { count = (int)(_length - _position); }
                long startBlockIndex = _position / _currentBlock.Length;
                long endBlockIndex = (_position + count - 1) / _currentBlock.Length;
                for (long blockIndex = startBlockIndex; blockIndex <= endBlockIndex; blockIndex++)
                {
                    lock (this)
                    {
                        byte[] blockData = ReadBlock((uint)blockIndex);
                        long blockStartPos = blockIndex * _currentBlock.Length;
                        long blockEndPos = blockStartPos + _currentBlockSize;
                        long readStartPos = blockStartPos; if (readStartPos < _position) { readStartPos = _position; }
                        long readEndPos = _position + count; if (readEndPos > blockEndPos) { readEndPos = blockEndPos; }
                        int readOffsetInBlock = (int)(readStartPos - blockStartPos);
                        int readLengthInBlock = (int)(readEndPos - readStartPos);
                        Array.Copy(blockData, readOffsetInBlock, buffer, offset + (int)(readStartPos - _position), readLengthInBlock);
                    }
                }
                _position += count;
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        _position = offset;
                        break;
                    case SeekOrigin.Current:
                        _position += offset;
                        break;
                    case SeekOrigin.End:
                        _position = _length + offset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("Invalid seek origin.");
                }
                return _position;
            }

            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void Close() { }
        }
        public class Block
        {
            uint _size;
            public uint Size { get { return _size; } }
            uint _index;
            public uint Index { get { return _index; } }
            internal Block(uint size, uint index)
            {
                _size = size;
                _index = index;
            }
        }
        Stream[] _streams;
        public Stream[] Streams { get { return _streams; } }
        internal MSF(Stream[] streams)
        {
            _streams = streams;
        }
        static byte[] MagicNumber { get { return System.Text.Encoding.UTF8.GetBytes("Microsoft C/C++ MSF 7.00\r\n\x1A\x44\x53\x00\x00\x00"); } }
        public static MSF Load(System.IO.BinaryReader stream)
        {
            long streamPosition = stream.BaseStream.Position;
            byte[] magicNumberRef = MagicNumber;
            byte[] magic = stream.ReadBytes(magicNumberRef.Length);
            if (magic == null || magic.Length != magicNumberRef.Length) { throw new System.Exception("Invalid MSF file: Magic number is missing or incomplete."); }
            for (int n = 0; n < magicNumberRef.Length; n++)
            {
                if (magic[n] != magicNumberRef[n]) { throw new System.Exception("Invalid MSF file: Magic number does not match."); }
            }
            uint blockSize = stream.ReadUInt32();
            uint freeBlockMapBlock = stream.ReadUInt32();
            uint numBlocks = stream.ReadUInt32();
            uint numDirectoryBytes = stream.ReadUInt32();
            uint unused = stream.ReadUInt32();
            uint blockMapAddr = stream.ReadUInt32();

            long streamDirectoryBlocksOffset = blockSize * blockMapAddr;
            stream.BaseStream.Position = streamPosition + streamDirectoryBlocksOffset;
            uint streamDirectoryBlockCount = (numDirectoryBytes + blockSize - 1) / blockSize;
            uint[] streamDirectoryBlocks = new uint[streamDirectoryBlockCount];
            for (uint n = 0; n < streamDirectoryBlockCount; n++) { streamDirectoryBlocks[n] = stream.ReadUInt32(); }
            Stream streamDirectory = new Stream(blockSize, streamDirectoryBlocks, numDirectoryBytes, stream.BaseStream, streamPosition);
            List<Stream> streams = new List<Stream>();
            using (System.IO.BinaryReader streamDirectoryReader = new System.IO.BinaryReader(streamDirectory, System.Text.Encoding.UTF8, true))
            {
                uint streamCount = streamDirectoryReader.ReadUInt32();
                uint[] streamSizes = new uint[streamCount];
                for (uint n = 0; n < streamCount; n++)
                {
                    streamSizes[n] = streamDirectoryReader.ReadUInt32();
                }
                for (uint n = 0; n < streamCount; n++)
                {
                    if (streamSizes[n] == 0xFFFFFFFF) { streams.Add(null); }
                    else
                    {
                        uint streamBlockCount = (streamSizes[n] + blockSize - 1) / blockSize;
                        uint[] streamBlocks = new uint[streamBlockCount];
                        for (uint m = 0; m < streamBlockCount; m++) { streamBlocks[m] = streamDirectoryReader.ReadUInt32(); }
                        streams.Add(new Stream(blockSize, streamBlocks, streamSizes[n], stream.BaseStream, streamPosition));
                    }
                }
            }
            return new MSF(streams.ToArray());
        }

        public static void Save(System.IO.BinaryWriter stream, System.IO.Stream[] dataStreams)
        {
            uint[] blocks = new uint[dataStreams.Length];
            long totalBlocks = 0;
            for (int n = 0; n < dataStreams.Length; n++)
            {
                long length = dataStreams[n].Length;
                long blockCount = (length + 4096 - 1) / 4096;
                totalBlocks += blockCount;
            }
            // We account for each blocks (totalBlocks) plus the stream count (1) and the stream sizes (dataStreams.Length), which are stored in the directory blocks.
            long directoryBlockLength = ((totalBlocks + 1 + dataStreams.Length) * 4);

            long directoryBlockBlockCount = (directoryBlockLength + 4096 - 1) / 4096;
            stream.Write(MagicNumber);
            uint blockSize = 4096; stream.Write(blockSize);
            uint freeBlockMapBlock = 1; stream.Write(freeBlockMapBlock);
            stream.Write((uint)totalBlocks);
            uint numDirectoryBytes = (uint)(directoryBlockLength); stream.Write(numDirectoryBytes);
            uint unused = 0; stream.Write(unused);
            uint blockMapAddr = 2; stream.Write(blockMapAddr);
            // The rest of the first block is filled with zeros (free blocks)
            for (int n = 56; n < 4096; n++) { stream.Write((byte)0); }
            // The free block should be ignored by most reader and we fill it with Zeros
            for (int n = 0; n < 4096; n++) { stream.Write((byte)0); }
            // Now we write the blocks of the directory block
            stream.Write((uint)dataStreams.Length);
            uint blockIndex = 0;
            for (int n = 0; n < dataStreams.Length; n++)
            {
                long length = dataStreams[n].Length;
                long blockCount = (length + 4096 - 1) / 4096;
                for (long m = 0; m < blockCount; m++) { stream.Write(blockIndex); blockIndex++; }
            }
            // Now we fill the rest of the directory block with zeros
            for (long n = directoryBlockLength; n < directoryBlockBlockCount * 4096; n++) { stream.Write((byte)0); }
            // Finally we write the data streams (padded)
            for (int n = 0; n < dataStreams.Length; n++)
            {
                long length = dataStreams[n].Length;
                long blockCount = (length + 4096 - 1) / 4096;
                byte[] buffer = new byte[4096];
                for (long m = 0; m < blockCount; m++)
                {
                    int bytesRead = dataStreams[n].Read(buffer, 0, buffer.Length);
                    if (bytesRead < buffer.Length)
                    {
                        // Pad the rest with zeros
                        for (int p = bytesRead; p < buffer.Length; p++) { buffer[p] = 0; }
                    }
                    stream.Write(buffer, 0, buffer.Length);
                }
            }
        }
    }
}