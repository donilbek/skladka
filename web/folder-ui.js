async function folderFromHandle(root) {
  const entries=[];
  async function visit(handle,path){
    if(handle.kind==='file'){entries.push({path,file:await handle.getFile()});return;}
    entries.push({path});
    for await(const child of handle.values())await visit(child,path+'/'+child.name);
  }
  await visit(root,root.name);
  return entries;
}
async function folderFromDrop(root) {
  const entries=[];
  async function visit(entry,path){
    if(entry.isFile){
      const file=await new Promise((ok,fail)=>entry.file(ok,fail));
      entries.push({path,file});return;
    }
    entries.push({path});
    const reader=entry.createReader();
    for(;;){
      const group=await new Promise((ok,fail)=>reader.readEntries(ok,fail));
      if(!group.length)break;
      for(const child of group)await visit(child,path+'/'+child.name);
    }
  }
  await visit(root,root.name);
  return entries;
}
function folderFromFiles(files){
  const entries=[],dirs=new Set();
  for(const file of files){
    const path=file.webkitRelativePath,parts=path.split('/');
    if(parts.length<2)throw new Error('Браузер не передал имя папки. Перетащите папку в окно.');
    for(let i=1;i<parts.length;i++){
      const dir=parts.slice(0,i).join('/');
      if(!dirs.has(dir)){dirs.add(dir);entries.push({path:dir});}
    }
    entries.push({path,file});
  }
  if(!entries.length)throw new Error('Для пустой папки используйте перетаскивание.');
  return entries;
}
async function saveFolderEntries(parent,entries){
  const name=entries[0].path;
  let rootName=name;
  for(let suffix=2;;suffix++){
    try{await parent.getDirectoryHandle(rootName);}
    catch(e){
      if(e.name==='NotFoundError')break;
      if(e.name!=='TypeMismatchError')throw e;
    }
    rootName=name+' ('+suffix+')';
  }
  const root=await parent.getDirectoryHandle(rootName,{create:true});
  const dirs=new Map([[name,root]]);
  for(const {path,file} of entries.slice(1)){
    const cut=path.lastIndexOf('/'),dir=dirs.get(path.slice(0,cut)),part=path.slice(cut+1);
    if(!file){dirs.set(path,await dir.getDirectoryHandle(part,{create:true}));continue;}
    const handle=await dir.getFileHandle(part,{create:true}),writer=await handle.createWritable();
    await file.stream().pipeTo(writer);
  }
  return rootName;
}
