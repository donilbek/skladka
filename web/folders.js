const pathEncoder=new TextEncoder();
function validateTree() {
  const seen=new Set(),dirs=new Set();
  return (path,dir)=>{
    const parts=path.split('/');
    for(const s of parts){
      if(!s || /[<>:"\\|?*\x00-\x1f]/.test(s) || /[. ]$/.test(s) ||
        /^(con|prn|aux|nul|conin\$|conout\$|(?:com|lpt)[0-9¹²³])(?:\.|$)/i.test(s))
        throw new Error('Недопустимое имя в папке: '+s);
    }
    const parent=parts.slice(0,-1).join('/');
    const key=path.normalize('NFC').toUpperCase();
    if(seen.has(key) || (seen.size===0 ? (!dir || parts.length!==1) : !dirs.has(parent)))
      throw new Error('Повторяющийся путь или нарушенная структура папки.');
    if(pathEncoder.encode(path).length+(dir?1:0)>65535) throw new Error('Слишком длинный путь.');
    seen.add(key);if(dir)dirs.add(path);
  };
}
function length64(n) {
  const bytes=new Uint8Array(8);
  new DataView(bytes.buffer).setBigUint64(0,BigInt(n),true);
  return bytes;
}
async function packFolder(entries,report){
  if(!entries.length)throw new Error('Не удалось прочитать выбранную папку.');
  const check=validateTree(),parts=[];
  let total=0;
  for(const {path,file} of entries){
    check(path,!file);
    const name=pathEncoder.encode(path);
    parts.push(integer(file?1:0,1),integer(name.length,2),name);
    if(file){parts.push(length64(file.size),file);total+=file.size;}
  }
  const packed=await processFile(new File(parts,entries[0].path),false,report,true);
  return {...packed,rawSize:total};
}
async function readFolder(blob){
  const check=validateTree(),entries=[];
  const decoder=new TextDecoder('utf-8',{fatal:true});
  let p=0,base=0,cache=new Uint8Array(),size=0;
  async function take(n){
    if(n>blob.size-p)throw invalid();
    if(p-base+n>cache.length){
      base=p;cache=new Uint8Array(await blob.slice(p,p+Math.max(n,65536)).arrayBuffer());
    }
    const data=cache.subarray(p-base,p-base+n);p+=n;return data;
  }
  while(p<blob.size){
    const header=await take(3),type=header[0];
    if(type>1)throw invalid();
    const path=decoder.decode(await take(header[1]+header[2]*256));
    check(path,type===0);
    let file=null;
    if(type===1){
      const length=await take(8);
      const n=Number(new DataView(length.buffer,length.byteOffset,8).getBigUint64(0,true));
      if(!Number.isSafeInteger(n) || n>blob.size-p)throw invalid();
      file=blob.slice(p,p+n);p+=n;size+=n;
    }
    entries.push({path,file});
  }
  if(!entries.length)throw invalid();
  return {entries,rawSize:size};
}

// Store verified output in a downloadable ZIP. ZIP64 is used only for fields
// that exceed the classic limits. The SKL compression has already been undone.
const crcTable=Uint32Array.from({length:256},(_,i)=>{
  let crc=i;
  for(let bit=0;bit<8;bit++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);
  return crc>>>0;
});
async function crcOf(blob){
  let crc=-1;
  const reader=blob.stream().getReader();
  try{
    for(;;){
      const {value,done}=await reader.read();if(done)break;
      for(let i=0;i<value.length;i++)crc=(crc>>>8)^crcTable[(crc^value[i])&255];
    }
  }finally{reader.releaseLock();}
  return (crc^-1)>>>0;
}
function zipFields(fields){
  const bytes=new Uint8Array(fields.reduce((n,[width])=>n+width,0));
  const view=new DataView(bytes.buffer);
  let p=0;
  for(const [width,value] of fields){
    if(width===2)view.setUint16(p,value,true);
    else if(width===4)view.setUint32(p,value,true);
    else view.setBigUint64(p,BigInt(value),true);
    p+=width;
  }
  return bytes;
}
function zipExtra(values){
  return values.length?zipFields([[2,1],[2,values.length*8],...values.map(v=>[8,v])]):new Uint8Array();
}
async function zipFolder(entries){
  const parts=[],central=[];
  let offset=0,usedWide=false;
  const max=0xffffffff;
  for(const {path,file} of entries){
    const name=pathEncoder.encode(file?path:path+'/');
    const size=file?file.size:0,wide=size>=max,far=offset>=max;
    const size32=Math.min(size,max),values=wide?[size,size]:[];
    const localExtra=zipExtra(values),centralExtra=zipExtra(far?[...values,offset]:values);
    const common=[[2,wide||far?45:20],[2,2048],[2,0],[2,0],[2,33],[4,file?await crcOf(file):0],[4,size32],[4,size32],[2,name.length]];
    const local=zipFields([[4,0x04034b50],...common,[2,localExtra.length]]);
    parts.push(local,name,localExtra);if(file)parts.push(file);
    central.push(zipFields([[4,0x02014b50],[2,45],...common,[2,centralExtra.length],[2,0],[2,0],[2,0],[4,file?0:16],[4,Math.min(offset,max)]]),name,centralExtra);
    offset+=local.length+name.length+localExtra.length+size;
    usedWide ||= wide||far;
  }
  const directory=new Blob(central),count=entries.length;
  parts.push(directory);
  if(usedWide || count>=65535 || offset>=max || directory.size>=max){
    parts.push(zipFields([[4,0x06064b50],[8,44],[2,45],[2,45],[4,0],[4,0],[8,count],[8,count],[8,directory.size],[8,offset]]));
    parts.push(zipFields([[4,0x07064b50],[4,0],[8,offset+directory.size],[4,1]]));
  }
  parts.push(zipFields([[4,0x06054b50],[2,0],[2,0],[2,Math.min(count,65535)],[2,Math.min(count,65535)],[4,Math.min(directory.size,max)],[4,Math.min(offset,max)],[2,0]]));
  return new Blob(parts,{type:'application/zip'});
}
