using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Skladka
{
    public static class Archive
    {
        const int Window = 32 * 1024, MaxBlock = 512 * 1024, HistoryLimit = 65535;
        static readonly byte[] Magic = { 83, 75, 76, 68 };
        static readonly byte[] Empty = new byte[0];
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        static InvalidDataException Broken()
        {
            return new InvalidDataException("Архив повреждён или имеет неизвестный формат.");
        }

        static byte[] Exact(BinaryReader reader, int n)
        {
            byte[] data = reader.ReadBytes(n);
            if (data.Length != n) throw Broken();
            return data;
        }

        static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static byte[] Deflate(byte[] bytes)
        {
            using (var dest = new MemoryStream())
            {
                using (var zip = new DeflateStream(dest, CompressionLevel.Optimal, true))
                    zip.Write(bytes, 0, bytes.Length);
                return dest.ToArray();
            }
        }

        // Commands 0..127 carry 1..128 literal bytes. Commands 128..255
        // copy 3..130 bytes from a little-endian 16-bit backward distance.
        static byte[] DictionaryPack(byte[] b, byte[] history)
        {
            int[] recent = new int[65536];
            int prefix = history.Length;
            if (prefix > 0)
            {
                byte[] joined = new byte[prefix + b.Length];
                Buffer.BlockCopy(history, 0, joined, 0, prefix);
                Buffer.BlockCopy(b, 0, joined, prefix, b.Length);
                b = joined;
                for (int i = 0; i < prefix && i + 2 < b.Length; i++)
                    recent[((b[i] * 251 + b[i+1]) * 251 + b[i+2]) & 65535] = i + 1;
            }
            using (var dest = new MemoryStream())
            {
                int pos = prefix, start = prefix;
                while (pos < b.Length)
                {
                    int length = 0, distance = 0;
                    if (pos + 2 < b.Length)
                    {
                        int key = ((b[pos] * 251 + b[pos + 1]) * 251 + b[pos + 2]) & 65535;
                        int from = recent[key] - 1;
                        recent[key] = pos + 1;
                        distance = pos - from;
                        if (from >= 0 && distance <= 65535)
                        {
                            int max = Math.Min(130, b.Length - pos);
                            while (length < max && b[from + length] == b[pos + length]) length++;
                        }
                    }
                    if (length >= 4)
                    {
                        if (start < pos) Literals(dest, b, start, pos);
                        dest.WriteByte((byte)(128 + length - 3));
                        dest.WriteByte((byte)distance);
                        dest.WriteByte((byte)(distance >> 8));
                        pos += length;
                        start = pos;
                    }
                    else if (++pos - start == 128)
                    {
                        Literals(dest, b, start, pos);
                        start = pos;
                    }
                }
                if (start < pos) Literals(dest, b, start, pos);
                return dest.ToArray();
            }
        }

        static void Literals(Stream dest, byte[] b, int start, int end)
        {
            dest.WriteByte((byte)(end - start - 1));
            dest.Write(b, start, end - start);
        }

        static byte[] DictionaryUnpack(byte[] b, int n, byte[] history)
        {
            byte[] result = new byte[n];
            int i = 0, j = 0;
            while (i < b.Length)
            {
                int tag = b[i++];
                if (tag < 128)
                {
                    int count = tag + 1;
                    if (count > n - j || count > b.Length - i) throw Broken();
                    Buffer.BlockCopy(b, i, result, j, count);
                    i += count;
                    j += count;
                }
                else
                {
                    int count = tag - 128 + 3;
                    if (i + 2 > b.Length || count > n - j) throw Broken();
                    int back = b[i] | (b[i + 1] << 8);
                    i += 2;
                    if (back == 0 || back > j + history.Length) throw Broken();
                    for (int k = 0; k < count; k++, j++)
                    {
                        int from = j - back;
                        result[j] = from < 0 ? history[history.Length + from] : result[from];
                    }
                }
            }
            if (j != n) throw Broken();
            return result;
        }

        static void Choose(ref byte mode, ref byte[] best, byte candidateMode, byte[] candidate)
        {
            if (candidate.Length >= best.Length) return;
            best = candidate;
            mode = candidateMode;
        }

        // A phrase may reuse earlier reconstructed bytes, including the beginning
        // of the same phrase. Corrections are applied before subsequent copies.
        static void PutNumber(Stream output, int value)
        {
            while (value >= 128)
            {
                output.WriteByte((byte)(128 | (value & 127)));
                value >>= 7;
            }
            output.WriteByte((byte)value);
        }

        static int GetNumber(byte[] input, ref int position)
        {
            int value = 0;
            // At most 2*MaxBlock for a tagged length; all fields fit in 21 bits.
            for (int shift = 0; shift < 21 && position < input.Length; shift += 7)
            {
                int b = input[position++];
                value |= (b & 127) << shift;
                if (b < 128) return value;
            }
            throw Broken();
        }

        static int Anchor(byte[] bytes, int p)
        {
            uint key = (uint)(bytes[p] | bytes[p+1] << 8 | bytes[p+2] << 16 | bytes[p+3] << 24);
            return (int)(unchecked(key * 2654435761U) >> 15);
        }

        static void TryTemplate(byte[] bytes, int pos, int from, ref int score, ref int length, ref int distance)
        {
            if (from < 0 || from >= pos) return;
            for (int i = 0; i < 4; i++) if (bytes[from+i] != bytes[pos+i]) return;
            int merit = 4, peak = 4, lastPeak = 3, end = Math.Min(bytes.Length-pos, 65536);
            for (int i = 4; i < end; i++)
            {
                merit += bytes[from+i] == bytes[pos+i] ? 1 : -5;
                if (merit > peak)
                {
                    peak = merit;
                    lastPeak = i;
                    if (merit > score)
                    {
                        score = merit;
                        length = i+1;
                        distance = pos-from;
                    }
                }
                if (peak-merit > 24 || i-lastPeak >= 64) break;
            }
        }

        static byte[] Templates(byte[] block, byte[] history)
        {
            int prefix = history.Length;
            byte[] all = new byte[prefix+block.Length];
            Buffer.BlockCopy(history,0,all,0,prefix);
            Buffer.BlockCopy(block,0,all,prefix,block.Length);
            int[] recent = new int[131072], older = new int[131072];
            int literal = prefix, next = prefix, lastDistance = 0;
            using (var output = new MemoryStream())
            {
                for (int pos=0; pos+4<=all.Length; pos++)
                {
                    int key = Anchor(all,pos);
                    int first = recent[key]-1, second = older[key]-1;
                    older[key] = recent[key]; recent[key] = pos+1;
                    if (pos < next) continue;
                    int score=9, length=0, distance=0;
                    TryTemplate(all,pos,first,ref score,ref length,ref distance);
                    TryTemplate(all,pos,second,ref score,ref length,ref distance);
                    int prior = pos-lastDistance;
                    if (lastDistance>0 && prior!=first && prior!=second)
                        TryTemplate(all,pos,prior,ref score,ref length,ref distance);
                    if (length==0) continue;
                    if (pos>literal)
                    {
                        PutNumber(output,(pos-literal)*2);
                        output.Write(all,literal,pos-literal);
                    }
                    PutNumber(output,length*2+1);
                    PutNumber(output,distance);
                    int changes=0;
                    for(int j=pos;j<pos+length;j++) if(all[j]!=all[j-distance]) changes++;
                    PutNumber(output,changes);
                    int previous=pos-1;
                    for(int j=pos;j<pos+length;j++)
                    {
                        int diff=all[j]^all[j-distance];
                        if(diff==0) continue;
                        PutNumber(output,j-previous-1);
                        output.WriteByte((byte)diff);
                        previous=j;
                    }
                    lastDistance=distance;
                    next=literal=pos+length;
                }
                if (lastDistance==0) return null;
                if(literal<all.Length)
                {
                    PutNumber(output,(all.Length-literal)*2);
                    output.Write(all,literal,all.Length-literal);
                }
                return output.Length <= block.Length+16 ? output.ToArray() : null;
            }
        }

        static byte[] ExpandTemplates(byte[] tokens, int n, byte[] history)
        {
            byte[] result = new byte[n];
            int p=0, outPos=0;
            while(p<tokens.Length)
            {
                int tag=GetNumber(tokens,ref p), length=tag>>1;
                if(length==0 || length>n-outPos) throw Broken();
                if((tag & 1)==0)
                {
                    if(length>tokens.Length-p) throw Broken();
                    Buffer.BlockCopy(tokens,p,result,outPos,length);
                    p+=length; outPos+=length;
                    continue;
                }
                int distance=GetNumber(tokens,ref p), changes=GetNumber(tokens,ref p);
                if(distance<1 || distance>outPos+history.Length || changes>length) throw Broken();
                int nextChange=changes==0 ? length : GetNumber(tokens,ref p);
                if(changes>0 && nextChange>=length) throw Broken();
                for(int j=0;j<length;j++,outPos++)
                {
                    int src=outPos-distance;
                    byte value=src<0 ? history[history.Length+src] : result[src];
                    if(j==nextChange)
                    {
                        if(p>=tokens.Length || tokens[p]==0) throw Broken();
                        value^=tokens[p++];
                        if(--changes>0)
                        {
                            nextChange=j+1+GetNumber(tokens,ref p);
                            if(nextChange>=length) throw Broken();
                        }
                    }
                    result[outPos]=value;
                }
            }
            if(outPos!=n) throw Broken();
            return result;
        }

        // Sample short runs spread throughout the region to retain byte-lane patterns.
        static double Entropy(byte[] b, int start, int length)
        {
            int[] histogram = new int[256];
            int count = 0, hop = Math.Max(64, (length / 32) & ~63);
            int end = start + length;
            for (int p = start; p < end; p += hop)
                for (int i = p; i < Math.Min(p+64, end); i++) { histogram[b[i]]++; count++; }
            double bits = 0;
            foreach (int h in histogram)
                if (h != 0) { double f = (double)h / count; bits -= f * Math.Log(f, 2); }
            return bits;
        }

        static int BlockLength(byte[] batch, int start)
        {
            double first = Entropy(batch, start, Math.Min(Window, batch.Length-start));
            for (int p = start + Window; p + Window <= batch.Length; p += Window)
                if (Math.Abs(Entropy(batch, p, Window) - first) > 0.8) return p-start;
            return batch.Length-start;
        }

        static byte[] Tail(byte[] history, byte[] block)
        {
            int length = Math.Min(HistoryLimit, history.Length + block.Length);
            byte[] tail = new byte[length];
            int current = Math.Min(length, block.Length), previous = length-current;
            Buffer.BlockCopy(history, history.Length-previous, tail, 0, previous);
            Buffer.BlockCopy(block, block.Length-current, tail, previous, current);
            return tail;
        }

        static byte[] Predict(byte[] input, bool inverse)
        {
            // Learn the last successor of each byte pair. Unseen contexts extrapolate the last slope.
            byte[] output = new byte[input.Length];
            int[] next = new int[65536];
            int a = 0, b = 0;
            for (int i = 0; i < input.Length; i++)
            {
                int key = a*256+b, seen = next[key];
                int prediction = seen == 0 ? 2*b-a : seen-1;
                byte actual = inverse ? (byte)(input[i]+prediction) : input[i];
                output[i] = inverse ? actual : (byte)(actual-prediction);
                next[key] = actual+1;
                a=b; b=actual;
            }
            return output;
        }

        static byte[] Shuffle(byte[] input, bool inverse)
        {
            int packed = 0;
            byte[] output = new byte[input.Length];
            for (int lane = 0; lane < 4; lane++)
                for (int i = lane; i < input.Length; i += 4, packed++)
                    if (inverse) output[i] = input[packed]; else output[packed] = input[i];
            return output;
        }

        static byte[] Encode(byte[] b, byte[] history, out byte mode)
        {
            mode = 0;
            bool uniform = true;
            for (int i = 1; i < b.Length && uniform; i++) uniform = b[i] == b[0];
            if (uniform) { mode = 1; return new byte[] { b[0] }; }
            byte[] best = b;
            double entropy = Entropy(b, 0, b.Length);
            Choose(ref mode, ref best, history.Length == 0 ? (byte)2 : (byte)64, DictionaryPack(b, history));
            Choose(ref mode, ref best, 3, Deflate(b));
            // Select the stride giving the smallest circular prediction error on a sample.
            int stride = 1;
            long least = long.MaxValue;
            foreach (int s in new int[] { 1, 2, 4, 8 })
            {
                long cost = 0;
                for (int i = 8; i < b.Length; i += 31)
                {
                    int d = Math.Abs(b[i] - b[i - s]);
                    cost += Math.Min(d, 256 - d);
                }
                if (cost < least) { least = cost; stride = s; }
            }
            byte[] delta = (byte[])b.Clone();
            for (int i = stride; i < b.Length; i++) delta[i] = (byte)(b[i] - b[i - stride]);
            if (entropy < 7.7 || Entropy(delta, 0, delta.Length) < 7.7)
            {
                Choose(ref mode, ref best, (byte)(16 + stride), Deflate(delta));
                Choose(ref mode, ref best, (byte)(32 + stride), DictionaryPack(delta, Empty));
            }
            byte[] residual = Predict(b, false);
            if (entropy < 7.7 || Entropy(residual, 0, b.Length) < 7.7)
                Choose(ref mode, ref best, 66, Deflate(residual));
            byte[] shuffled = Shuffle(b, false);
            int laneLength = (b.Length+3)/4;
            if (entropy < 7.7 || Entropy(shuffled, 0, laneLength) < 7.7 ||
                Entropy(shuffled, b.Length-laneLength, laneLength) < 7.7)
                Choose(ref mode, ref best, 65, Deflate(shuffled));
            byte[] tokens = b.Length >= 10 ? Templates(b, history) : null;
            if (tokens != null)
            {
                Choose(ref mode, ref best, 70, tokens);
                byte[] coded = Deflate(tokens);
                if (coded.Length+4 < best.Length)
                {
                    using (var dest = new MemoryStream())
                    using (var writer = new BinaryWriter(dest))
                    {
                        writer.Write(tokens.Length);
                        writer.Write(coded);
                        best = dest.ToArray();
                        mode = 71;
                    }
                }
            }
            return best;
        }

        static byte[] Decode(byte mode, byte[] payload, int n, byte[] history)
        {
            if (mode == 0 && payload.Length == n) return payload;
            if (mode == 1 && payload.Length == 1)
            {
                byte[] b = new byte[n];
                for (int i = 0; i < n; i++) b[i] = payload[0];
                return b;
            }
            if (mode == 2) return DictionaryUnpack(payload, n, Empty);
            if (mode == 64) return DictionaryUnpack(payload, n, history);
            if (mode == 33 || mode == 34 || mode == 36 || mode == 40)
                return RestoreDelta(DictionaryUnpack(payload, n, Empty), mode-32);
            if (mode == 70) return ExpandTemplates(payload,n,history);
            if (mode == 71)
            {
                if(payload.Length<5) throw Broken();
                int size=payload[0] | payload[1]<<8 | payload[2]<<16 | payload[3]<<24;
                if(size<=0 || size>n+16) throw Broken();
                return ExpandTemplates(Inflate(payload,4,size),n,history);
            }
            if (mode != 3 && mode != 17 && mode != 18 && mode != 20 && mode != 24 &&
                mode != 65 && mode != 66) throw Broken();
            byte[] restored = Inflate(payload,0,n);
            if (mode == 65) return Shuffle(restored, true);
            if (mode == 66) return Predict(restored, true);
            return mode == 3 ? restored : RestoreDelta(restored, mode-16);
        }

        static byte[] Inflate(byte[] payload, int start, int n)
        {
            byte[] b=new byte[n];
            using (var input = new MemoryStream(payload,start,payload.Length-start))
            using (var zip = new DeflateStream(input, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < n)
                {
                    int got = zip.Read(b, offset, n - offset);
                    if (got == 0) throw Broken();
                    offset += got;
                }
                if (zip.ReadByte() != -1) throw Broken();
            }
            return b;
        }

        static byte[] RestoreDelta(byte[] bytes, int stride)
        {
            for (int i=stride; i<bytes.Length; i++) bytes[i] += bytes[i-stride];
            return bytes;
        }

        static void WriteBlock(BinaryWriter writer, byte[] block, byte[] history)
        {
            byte mode;
            byte[] payload = Encode(block, history, out mode);
            writer.Write(block.Length);
            writer.Write(mode);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        static void WriteBatch(BinaryWriter writer, byte[] batch, byte[] history)
        {
            int first = BlockLength(batch,0);
            if (first == batch.Length)
            {
                WriteBlock(writer,batch,history);
                return;
            }
            byte[] localHistory = history;
            using (var stream = new MemoryStream())
            using (var split = new BinaryWriter(stream))
            {
                for (int start=0; start<batch.Length;)
                {
                    int n = start==0 ? first : BlockLength(batch,start);
                    byte[] block = new byte[n];
                    Buffer.BlockCopy(batch,start,block,0,n);
                    WriteBlock(split,block,localHistory);
                    localHistory = Tail(localHistory,block);
                    start += n;
                }
                // Compare full records, including their nine-byte block headers.
                byte mode;
                byte[] whole = Encode(batch,history,out mode);
                if (whole.Length+9 < stream.Length)
                {
                    writer.Write(batch.Length);
                    writer.Write(mode);
                    writer.Write(whole.Length);
                    writer.Write(whole);
                }
                else writer.Write(stream.GetBuffer(),0,(int)stream.Length);
            }
        }

        public static void Pack(Stream input, Stream output, string name, Action<int> progress)
        {
            Pack(input,output,name,progress,false);
        }

        public static void Pack(Stream input, Stream output, string name, Action<int> progress, bool folder)
        {
            long length = input.Length;
            byte[] filename = Utf8.GetBytes(Path.GetFileName(name) ?? "");
            if (filename.Length > 1024) throw new InvalidDataException("Слишком длинное имя файла.");
            using (var sha = SHA256.Create())
            using (var reader = new BinaryReader(input, Utf8, true))
            using (var writer = new BinaryWriter(output, Utf8, true))
            {
                writer.Write(Magic);
                bool wide = folder || length > Int32.MaxValue;
                writer.Write((byte)(folder ? 5 : wide ? 4 : 3));
                if (wide) writer.Write(length); else writer.Write((int)length);
                writer.Write((ushort)filename.Length);
                writer.Write(filename);
                byte[] history = Empty;
                for (long pos = 0; pos < length;)
                {
                    byte[] batch = Exact(reader, (int)Math.Min(MaxBlock, length-pos));
                    sha.TransformBlock(batch,0,batch.Length,batch,0);
                    WriteBatch(writer,batch,history);
                    history = Tail(history,batch);
                    pos += batch.Length;
                    if (progress != null) progress((int)(100.0 * pos / length));
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                writer.Write(sha.Hash);
            }
        }

        public static string Unpack(Stream input, Stream output, Action<int> progress)
        {
            bool folder;
            return Unpack(input,output,progress,out folder);
        }

        public static string Unpack(Stream input, Stream output, Action<int> progress, out bool folder)
        {
            using (var reader = new BinaryReader(input, Utf8, true))
            using (var sha = SHA256.Create())
            {
                if (!Equal(Exact(reader, Magic.Length), Magic)) throw Broken();
                int version = reader.ReadByte();
                if (version == 1 || version == 2)
                    throw new InvalidDataException("Формат архива не поддерживается.");
                folder = version == 5;
                if (version != 3 && version != 4 && !folder) throw Broken();
                long length = version == 3 ? reader.ReadInt32() : reader.ReadInt64();
                int nameLength = reader.ReadUInt16();
                if (length < 0 || nameLength > 1024) throw Broken();
                string name = Utf8.GetString(Exact(reader, nameLength));
                byte[] history = Empty;
                for (long pos = 0; pos < length;)
                {
                    long left = length-pos;
                    int n = reader.ReadInt32();
                    if (n <= 0 || n > MaxBlock || n > left || (n < Window && n != left)) throw Broken();
                    byte mode = reader.ReadByte();
                    int count = reader.ReadInt32();
                    if (count < 1 || count > n) throw Broken();
                    byte[] b = Decode(mode, Exact(reader, count), n, history);
                    sha.TransformBlock(b, 0, b.Length, b, 0);
                    output.Write(b, 0, n);
                    history = Tail(history, b);
                    pos += n;
                    if (progress != null) progress((int)(100.0 * pos / length));
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (!Equal(Exact(reader, 32), sha.Hash) || input.ReadByte() != -1) throw Broken();
                return name;
            }
        }
    }
}
