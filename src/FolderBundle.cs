using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Skladka
{
    public sealed class FolderBundle
    {
        sealed class Entry
        {
            public string Path;
            public bool Directory;
            public long Offset, Length;
        }
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false,true);
        static readonly Regex Device = new Regex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[0-9¹²³])(?:\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        readonly List<Entry> entries = new List<Entry>();
        public string Root { get { return entries[0].Path; } }
        public long Size { get; private set; }

        static void CheckName(string s)
        {
            if (s.Length==0 || s.EndsWith(".") || s.EndsWith(" ") || Device.IsMatch(s))
                throw new InvalidDataException("Недопустимое имя в папке: " + s);
            foreach(char c in s)
                if(c<32 || "<>:\"/\\|?*".IndexOf(c)>=0) throw new InvalidDataException("Недопустимый символ в имени.");
        }
        static void Store(FileSystemInfo item, string relative, BinaryWriter writer, ref long total)
        {
            CheckName(item.Name);
            if ((item.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new IOException("Ссылка в выбранной папке: " + item.FullName);
            byte[] path=Utf8.GetBytes(relative);
            var dir=item as DirectoryInfo;
            if(path.Length+(dir==null?0:1)>65535) throw new IOException("Слишком длинный путь: " + relative);
            writer.Write((byte)(dir==null ? 1 : 0)); writer.Write((ushort)path.Length); writer.Write(path);
            if(dir!=null)
            {
                foreach(var child in dir.EnumerateFileSystemInfos()) Store(child,relative+"/"+child.Name,writer,ref total);
            }
            else using(var source=File.OpenRead(item.FullName))
            {
                writer.Write(source.Length);
                total=checked(total+source.Length);
                source.CopyTo(writer.BaseStream);
            }
        }
        public static long Pack(string path, Stream output, Action<int> progress)
        {
            var root=new DirectoryInfo(path);
            using(var data=Temp())
            using(var writer=new BinaryWriter(data,Utf8,true))
            {
                long total=0;
                Store(root,root.Name,writer,ref total);
                writer.Flush();data.Position=0;
                // Check collisions and portable path rules before emitting the archive.
                Read(data);data.Position=0;
                Archive.Pack(data,output,"",progress,true);
                return total;
            }
        }
        public static FileStream Temp()
        {
            return new FileStream(Path.Combine(Path.GetTempPath(),Path.GetRandomFileName()),FileMode.CreateNew,
                FileAccess.ReadWrite,FileShare.None,65536,FileOptions.SequentialScan|FileOptions.DeleteOnClose);
        }
        public static FolderBundle Read(Stream data)
        {
            var result=new FolderBundle();
            var known=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            var exactDirs=new HashSet<string>(StringComparer.Ordinal);
            data.Position=0;
            using(var reader=new BinaryReader(data,Utf8,true))
            while(data.Position<data.Length)
            {
                int type=reader.ReadByte(), length=reader.ReadUInt16();
                byte[] encoded=reader.ReadBytes(length);
                if(type>1 || encoded.Length!=length || (type==0 && length==65535)) throw new InvalidDataException("Повреждена запись папки.");
                string path=Utf8.GetString(encoded);
                string[] parts=path.Split('/');
                foreach(var part in parts) CheckName(part);
                int cut=path.LastIndexOf('/');
                if(result.entries.Count==0 ? (type!=0 || cut>=0) : (cut<0 || !exactDirs.Contains(path.Substring(0,cut))))
                    throw new InvalidDataException("Нарушена структура каталогов.");
                string key=path.Normalize(NormalizationForm.FormC);
                if(known.ContainsKey(key)) throw new InvalidDataException("Повторяющийся путь: "+path);
                known.Add(key,type==0);
                long size=type==0 ? 0 : reader.ReadInt64();
                if(size<0 || size>data.Length-data.Position) throw new InvalidDataException("Неверный размер файла в папке.");
                if(type==0) exactDirs.Add(path);
                result.entries.Add(new Entry { Path=path,Directory=type==0,Offset=data.Position,Length=size });
                result.Size+=size;
                data.Position+=size;
            }
            if(result.entries.Count==0)throw new InvalidDataException("В архиве отсутствует корневая папка.");
            return result;
        }
        static void Copy(Stream data, Entry entry, Stream output, byte[] buffer)
        {
            data.Position=entry.Offset;
            for(long left=entry.Length;left>0;)
            {
                int n=data.Read(buffer,0,(int)Math.Min(left,buffer.Length));
                if(n==0)throw new EndOfStreamException();
                output.Write(buffer,0,n);left-=n;
            }
        }
        public void ToZip(Stream data, Stream output)
        {
            byte[] buffer=new byte[65536];
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true,Utf8))
            foreach(var e in entries)
            {
                var entry=zip.CreateEntry(e.Path+(e.Directory?"/":""),CompressionLevel.NoCompression);
                if(!e.Directory)using(var dest=entry.Open()) Copy(data,e,dest,buffer);
            }
        }
        public string Save(Stream data,string parent)
        {
            string stage=Path.Combine(parent,".skladka-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                byte[] buffer=new byte[65536];
                foreach(var entry in entries)
                {
                    string target=Path.Combine(stage,entry.Path.Replace('/',Path.DirectorySeparatorChar));
                    if(entry.Directory)Directory.CreateDirectory(target);
                    else using(var dest=new FileStream(target,FileMode.CreateNew,FileAccess.Write))
                        Copy(data,entry,dest,buffer);
                }
                string name=Root;
                for(int i=2;;i++)
                {
                    string target=Path.Combine(parent,name);
                    if(!Directory.Exists(target) && !File.Exists(target))
                    {
                        try {Directory.Move(Path.Combine(stage,Root),target);return target;}
                        catch(IOException){if(!Directory.Exists(target) && !File.Exists(target))throw;}
                    }
                    name=Root+" ("+i+")";
                }
            }
            finally { Directory.Delete(stage,true); }
        }
    }
}
