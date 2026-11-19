const WINDOW = 32 * 1024, MAX_BLOCK = 512 * 1024, HISTORY = 65535;
const EMPTY = new Uint8Array();
const invalid = () => new Error('Архив повреждён или имеет неизвестный формат.');
function integer(value, size) {
  const bytes = new Uint8Array(size);
  for (let i=0; i<size; i++) bytes[i] = value >>> (8*i);
  return bytes;
}
function dictionary(bytes,history=EMPTY) {
  const recent = new Int32Array(65536), out = new Uint8Array(bytes.length + Math.ceil(bytes.length / 128) + 16);
  const prefix=history.length;
  if(prefix) {
    const joined=new Uint8Array(prefix+bytes.length);
    joined.set(history); joined.set(bytes,prefix); bytes=joined;
    for(let i=0;i<prefix && i+2<bytes.length;i++)
      recent[((bytes[i]*251+bytes[i+1])*251+bytes[i+2])&65535]=i+1;
  }
  let pos=prefix, start=prefix, used=0;
  function emitLiteral() {
    if (pos === start) return;
    out[used++] = pos-start-1;
    out.set(bytes.subarray(start,pos),used);
    used += pos-start;
  }
  while (pos < bytes.length) {
    let count=0, back=0;
    if (pos+2 < bytes.length) {
      const key = ((bytes[pos]*251+bytes[pos+1])*251+bytes[pos+2]) & 65535;
      const from = recent[key]-1;
      recent[key]=pos+1; back=pos-from;
      if (from>=0 && back<=65535) {
        const max=Math.min(130,bytes.length-pos);
        while(count<max && bytes[from+count]===bytes[pos+count]) count++;
      }
    }
    if (count>=4) {
      emitLiteral();
      out[used++]=128+count-3; out[used++]=back; out[used++]=back>>>8;
      pos+=count; start=pos;
    } else {
      pos++;
      if (pos-start===128) {emitLiteral(); start=pos;}
    }
  }
  emitLiteral();
  return out.slice(0,used);
}
async function deflate(bytes) {
  const stream = new Blob([bytes]).stream().pipeThrough(new CompressionStream('deflate-raw'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}
function entropy(bytes) {
  const bins=new Uint32Array(256);
  const hop=Math.max(64,Math.floor(bytes.length/2048)*64);
  let count=0;
  for(let p=0;p<bytes.length;p+=hop) {
    const end=Math.min(bytes.length,p+64);
    for(let i=p;i<end;i++) {bins[bytes[i]]++;count++;}
  }
  let bits=0;
  for(const hits of bins) if(hits){const f=hits/count;bits-=f*Math.log2(f);}
  return bits;
}
function blockLength(batch) {
  const first=entropy(batch.subarray(0,WINDOW));
  for(let p=WINDOW;p+WINDOW<=batch.length;p+=WINDOW)
    if(Math.abs(entropy(batch.subarray(p,p+WINDOW))-first)>.8) return p;
  return batch.length;
}
function predict(input,inverse) {
  // Each two-byte context predicts its last observed successor. Unseen pairs use the last slope.
  const next=new Uint16Array(65536),out=new Uint8Array(input.length);
  let a=0,b=0;
  for(let i=0;i<input.length;i++){
    const key=a*256+b,seen=next[key],guess=seen?seen-1:2*b-a;
    const actual=inverse?(input[i]+guess)&255:input[i];
    out[i]=inverse?actual:actual-guess;
    next[key]=actual+1;a=b;b=actual;
  }
  return out;
}
function shuffle(input,inverse) {
  const out=new Uint8Array(input.length);
  let packed=0;
  for(let lane=0;lane<4;lane++)
    for(let i=lane;i<input.length;i+=4,packed++)
      if(inverse)out[i]=input[packed];else out[packed]=input[i];
  return out;
}
// A phrase describes a previous fragment and the individual bytes to correct.
function templates(block,history) {
  const prefix=history.length, all=new Uint8Array(prefix+block.length);
  all.set(history);all.set(block,prefix);
  const recent=new Int32Array(131072),older=new Int32Array(131072);
  const tokens=[];
  function number(value) {
    while(value>=128){tokens.push(128|(value&127));value>>>=7;}
    tokens.push(value);
  }
  function literals(start,end) {
    if(start===end) return;
    number(2*(end-start));
    for(let i=start;i<end;i++) tokens.push(all[i]);
  }
  let literal=prefix,next=prefix,lastDistance=0;
  let score,length,distance;
  function tryTemplate(pos,from) {
    if(from<0 || from>=pos) return;
    for(let i=0;i<4;i++) if(all[from+i]!==all[pos+i]) return;
    let merit=4,peak=4,lastPeak=3;
    const end=Math.min(all.length-pos,65536);
    for(let i=4;i<end;i++) {
      merit+=all[from+i]===all[pos+i]?1:-5;
      if(merit>peak) {
        peak=merit;lastPeak=i;
        if(merit>score){score=merit;length=i+1;distance=pos-from;}
      }
      if(peak-merit>24 || i-lastPeak>=64) break;
    }
  }
  for(let pos=0;pos+4<=all.length;pos++) {
    const word=all[pos]|all[pos+1]<<8|all[pos+2]<<16|all[pos+3]<<24;
    const key=Math.imul(word,2654435761)>>>15;
    const first=recent[key]-1,second=older[key]-1;
    older[key]=recent[key];recent[key]=pos+1;
    if(pos<next) continue;
    score=9;length=0;distance=0;
    tryTemplate(pos,first);tryTemplate(pos,second);
    const prior=pos-lastDistance;
    if(lastDistance && prior!==first && prior!==second) tryTemplate(pos,prior);
    if(!length) continue;
    literals(literal,pos);
    number(length*2+1);number(distance);
    let changes=0;
    for(let j=pos;j<pos+length;j++) if(all[j]!==all[j-distance]) changes++;
    number(changes);
    let previous=pos-1;
    for(let j=pos;j<pos+length;j++) {
      const diff=all[j]^all[j-distance];
      if(diff){number(j-previous-1);tokens.push(diff);previous=j;}
    }
    lastDistance=distance;
    next=literal=pos+length;
  }
  if(!lastDistance) return null;
  literals(literal,all.length);
  return tokens.length<=block.length+16?Uint8Array.from(tokens):null;
}
function expandTemplates(tokens,n,history) {
  const result=new Uint8Array(n);
  let p=0,outPos=0;
  function number(){
    let value=0;
    for(let shift=0;shift<21 && p<tokens.length;shift+=7){
      const b=tokens[p++];value|=(b&127)<<shift;
      if(b<128) return value;
    }
    throw invalid();
  }
  while(p<tokens.length){
    const tag=number(),length=tag>>>1;
    if(!length || length>n-outPos) throw invalid();
    if(!(tag&1)){
      if(length>tokens.length-p) throw invalid();
      result.set(tokens.subarray(p,p+length),outPos);
      p+=length;outPos+=length;
      continue;
    }
    const distance=number();
    let changes=number();
    if(!distance || distance>outPos+history.length || changes>length) throw invalid();
    let nextChange=changes?number():length;
    if(changes && nextChange>=length) throw invalid();
    for(let j=0;j<length;j++,outPos++){
      const src=outPos-distance;
      let value=src<0?history[history.length+src]:result[src];
      if(j===nextChange){
        if(p>=tokens.length || !tokens[p]) throw invalid();
        value^=tokens[p++];
        if(--changes>0){
          nextChange=j+1+number();
          if(nextChange>=length) throw invalid();
        }
      }
      result[outPos]=value;
    }
  }
  if(outPos!==n) throw invalid();
  return result;
}
async function encode(b,history) {
  if(b.every(v=>v===b[0])) return [1,b.slice(0,1)];
  let best=b, mode=0;
  function choose(m,v) {if(v.length<best.length) {mode=m; best=v;}}
  const bits=entropy(b);
  choose(history.length?64:2,dictionary(b,history));
  choose(3,await deflate(b));
  let step=1, least=Infinity;
  for(const s of [1,2,4,8]) {
    let cost=0;
    for(let i=8;i<b.length;i+=31) {
      const d=Math.abs(b[i]-b[i-s]); cost+=Math.min(d,256-d);
    }
    if(cost<least){least=cost;step=s;}
  }
  const delta=b.slice();
  for(let i=step;i<b.length;i++) delta[i]=b[i]-b[i-step];
  if(bits<7.7 || entropy(delta)<7.7) {
    choose(16+step,await deflate(delta));
    choose(32+step,dictionary(delta));
  }
  const residual=predict(b,false);
  if(bits<7.7 || entropy(residual)<7.7) choose(66,await deflate(residual));
  const shuffled=shuffle(b,false),lane=Math.ceil(b.length/4);
  if(bits<7.7 || entropy(shuffled.subarray(0,lane))<7.7 || entropy(shuffled.subarray(-lane))<7.7)
    choose(65,await deflate(shuffled));
  const tokens=b.length>=10?templates(b,history):null;
  if(tokens){
    choose(70,tokens);
    const coded=await deflate(tokens);
    if(coded.length+4<best.length){
      const framed=new Uint8Array(coded.length+4);
      framed.set(integer(tokens.length,4));framed.set(coded,4);
      choose(71,framed);
    }
  }
  return [mode,best];
}
async function decode(mode,b,n,history) {
  if(mode===0 && b.length===n) return b;
  if(mode===1 && b.length===1) return new Uint8Array(n).fill(b[0]);
  if(mode===70) return expandTemplates(b,n,history);
  if(mode===71) {
    if(b.length<5) throw invalid();
    const size=new DataView(b.buffer,b.byteOffset,4).getUint32(0,true);
    if(!size || size>n+16) throw invalid();
    return expandTemplates(await inflate(b.subarray(4),size),n,history);
  }
  const deltaDict=[33,34,36,40].includes(mode);
  if(mode===2 || deltaDict || mode===64) {
    const out=new Uint8Array(n);
    const prefix=mode===64?history:EMPTY;
    let p=0,j=0;
    while(p<b.length) {
      const t=b[p++], count=t<128?t+1:t-125;
      if(j+count>n) throw invalid();
      if(t<128) {
        if(p+count>b.length) throw invalid();
        out.set(b.subarray(p,p+count),j); p+=count; j+=count;
      } else {
        if(p+2>b.length) throw invalid();
        const back=b[p++] | (b[p++]<<8);
        if(!back || back>j+prefix.length) throw invalid();
        for(let k=0;k<count;k++,j++) {
          const from=j-back;
          out[j]=from<0?prefix[prefix.length+from]:out[from];
        }
      }
    }
    if(j!==n) throw invalid();
    if(deltaDict) for(let i=mode-32;i<n;i++) out[i]+=out[i-(mode-32)];
    return out;
  }
  if(![3,17,18,20,24,65,66].includes(mode)) throw invalid();
  const out=await inflate(b,n);
  if(mode===65) return shuffle(out,true);
  if(mode===66) return predict(out,true);
  if(mode>16) for(let i=mode-16;i<n;i++) out[i]+=out[i-(mode-16)];
  return out;
}
async function inflate(b,n) {
  const out=new Uint8Array(n);
  const reader=new Blob([b]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
  let j=0;
  try {
    for(;;) {
      const {value,done}=await reader.read();
      if(done) break;
      if(j+value.length>n) {await reader.cancel();throw invalid();}
      out.set(value,j);j+=value.length;
    }
  } catch {throw invalid();}
  if(j!==n) throw invalid();
  return out;
}
async function packBatch(bytes,start,end) {
  const blocks=[];
  let splitSize=0;
  async function record(offset,length) {
    const history=bytes.subarray(Math.max(0,offset-HISTORY),offset);
    const [mode,payload]=await encode(bytes.subarray(offset,offset+length),history);
    return [integer(length,4),integer(mode,1),integer(payload.length,4),payload];
  }
  for(let pos=start;pos<end;) {
    const n=blockLength(bytes.subarray(pos,end));
    const block=await record(pos,n);
    blocks.push(...block);
    splitSize+=9+block[3].length;
    pos+=n;
  }
  if(blocks.length>4){
    const whole=await record(start,end-start);
    if(9+whole[3].length<splitSize) return whole;
  }
  return blocks;
}
async function processFile(file,extract,report,folder=false) {
  const magic=[83,75,76,68];
  const sha=new Sha256();
  let history=EMPTY;
  const parts=[];
  function withHistory(block) {
    const joined=new Uint8Array(history.length+block.length);
    joined.set(history);joined.set(block,history.length);
    return joined;
  }
  if(!Number.isSafeInteger(file.size)) throw new Error('Браузер не может точно представить размер файла.');
  if(!extract) {
    const name=new TextEncoder().encode(file.name);
    if(name.length>1024) throw new Error('Слишком длинное имя файла.');
    const wide=folder || file.size>0x7fffffff;
    const length=wide?new Uint8Array(8):integer(file.size,4);
    if(wide)new DataView(length.buffer).setBigInt64(0,BigInt(file.size),true);
    parts.push(new Uint8Array([...magic,folder?5:wide?4:3]),length,integer(name.length,2),name);
    for(let i=0;i<file.size;i+=MAX_BLOCK) {
      const end=Math.min(i+MAX_BLOCK,file.size);
      const block=new Uint8Array(await file.slice(i,end).arrayBuffer());
      if(block.length!==end-i)throw new Error('Не удалось прочитать файл целиком.');
      sha.update(block);
      const bytes=withHistory(block);
      parts.push(new Blob(await packBatch(bytes,history.length,bytes.length)));
      history=bytes.slice(-HISTORY);
      report(Math.min(99,Math.round(end/file.size*100)));
    }
    parts.push(sha.finish());
    return {blob:new Blob(parts),name:file.name+'.skl'};
  }
  let p=0,base=0,buffer=EMPTY;
  async function take(n) {
    if(n>file.size-p) throw invalid();
    if(p-base+n>buffer.length) {
      base=p;
      buffer=new Uint8Array(await file.slice(p,p+Math.max(MAX_BLOCK,n)).arrayBuffer());
      if(n>buffer.length) throw invalid();
    }
    const relative=p-base;
    p+=n;
    return buffer.subarray(relative,relative+n);
  }
  async function read(n) {return (await take(n)).reduce((v,b,i)=>v+b*2**(8*i),0);}
  const prefix=await take(5);
  if(!magic.every((v,i)=>v===prefix[i])) throw invalid();
  const version=prefix[4];
  if(version===1 || version===2)throw new Error('Формат архива не поддерживается.');
  if(version!==3 && version!==4 && version!==5) throw invalid();
  const size=await read(version===3?4:8),nameSize=await read(2);
  if(!Number.isSafeInteger(size) || (version===3 && size>0x7fffffff) || nameSize>1024) throw invalid();
  let name;
  try {name=new TextDecoder('utf-8',{fatal:true}).decode(await take(nameSize));} catch {throw invalid();}
  if(!name || /[<>:"/\\|?*\x00-\x1f]/.test(name) || /^\.+$/.test(name)) name='result.bin';
  for(let i=0;i<size;) {
    const left=size-i,n=await read(4);
    if(!n || n>MAX_BLOCK || n>left || (n<WINDOW && n!==left)) throw invalid();
    const mode=await read(1),length=await read(4);
    if(length<1 || length>n) throw invalid();
    const block=await decode(mode,await take(length),n,history);
    sha.update(block);
    parts.push(new Blob([block]));
    history=block.length>=HISTORY?block.slice(-HISTORY):withHistory(block).slice(-HISTORY);
    i+=n;
    report(Math.min(99,Math.round(i/size*100)));
  }
  const saved=await take(32),hash=sha.finish();
  if(p!==file.size || !hash.every((b,i)=>b===saved[i])) throw invalid();
  const restored=new Blob(parts);
  if(version===5){
    const tree=await readFolder(restored);
    return {...tree,blob:await zipFolder(tree.entries),name:tree.entries[0].path+'.zip'};
  }
  return {blob:restored,name};
}
self.onmessage=async ({data})=>{
  try {
    if(typeof CompressionStream==='undefined' || typeof DecompressionStream==='undefined') throw new Error('Откройте страницу в актуальной версии Chrome, Edge, Firefox или Safari.');
    const report=progress=>self.postMessage({progress});
    self.postMessage(data.entries?await packFolder(data.entries,report):await processFile(data.file,data.extract,report));
  } catch(error) {self.postMessage({error:error.message || 'Не удалось обработать файл.'});}
};
