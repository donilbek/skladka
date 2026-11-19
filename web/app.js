(() => {
  const by=id=>document.getElementById(id);
  const drop=by('drop'),pick=by('fileInput'),main=by('primary'),sample=by('demoButton'),bar=by('progress');
  const workerUrl=URL.createObjectURL(new Blob([by('worker').textContent],{type:'text/javascript'}));
  let extract=false,phase='idle',worker=null,result=null,current=null,downloadUrl='',drags=0;
  let selection=0;
  const folderButton=by('folderButton'),folderInput=by('folderInput');
  const fmt=n=>new Intl.NumberFormat('ru-RU',{maximumFractionDigits:1}).format(n);
  const size=n=>n>=1073741824?fmt(n/1073741824)+' ГиБ':n>=1048576?fmt(n/1048576)+' МиБ':n>=1024?fmt(n/1024)+' КиБ':n+' Б';
  function setIcon(id,name){by(id).setAttribute('href','#'+name);}
  function clear(){
    selection++;
    worker?.terminate();worker=null;
    if(downloadUrl) URL.revokeObjectURL(downloadUrl);
    downloadUrl='';result=null;current=null;phase='idle';
    by('original').textContent=by('packed').textContent=by('saving').textContent='—';
    bar.hidden=true;bar.value=0;drop.classList.remove('error','drag');drags=0;pick.value='';folderInput.value='';
    folderButton.hidden=extract;folderButton.textContent='Выбрать папку';
  }
  function view(title,desc,button,icon,actionIcon,foot,footIcon) {
    by('dropTitle').textContent=title;by('dropText').textContent=desc;
    by('buttonText').textContent=button;by('footText').textContent=foot;
    setIcon('stateicon',icon);setIcon('buttonIcon',actionIcon);setIcon('footIcon',footIcon);
    by('announcement').textContent=title+'. '+desc;
  }
  function idle(){
    clear();setArtState('idle',extract);
    by('compress').setAttribute('aria-pressed',String(!extract));
    by('decompress').setAttribute('aria-pressed',String(extract));
    if(extract) pick.accept='.skl';else pick.removeAttribute('accept');
    view(extract?'Перетащите архив сюда':'Перетащите файл или папку',
      extract?'Архив «Складки» в формате .skl':'Папка сохранится вместе с подпапками',extract?'Выбрать архив':'Выбрать файл',
      'upload','folder','Попробовать на примере','sample');
  }
  function error(message) {
    clear();phase='error';drop.classList.add('error');setArtState('idle',extract);
    view('Не удалось обработать файл',message,'Выбрать другой файл','file','folder','Попробовать на примере','sample');
  }
  function run(file,entries,filesOnly=false){
    if(!file) return;
    clear();current=file;phase='working';folderButton.hidden=true;
    by(extract?'packed':'original').textContent=size(file.size);
    setArtState('working',extract,0);
    view(extract?'Распаковываем…':'Сжимаем…',file.name,'Отменить','layers','x','Выбрать другой файл','folder');
    bar.value=0;bar.hidden=false;
    try {
      worker=new Worker(workerUrl);
      worker.onerror=()=>error('Не удалось запустить обработку. Откройте страницу в актуальной версии браузера.');
      worker.onmessage=({data})=>{
        if('progress' in data){bar.value=data.progress;setArtState('working',extract,data.progress);return;}
        if(data.error){error(data.error);return;}
        worker.terminate();worker=null;result=data;phase='done';bar.hidden=true;
        downloadUrl=URL.createObjectURL(data.blob);setArtState('done',extract);
        const raw=data.rawSize??(extract?data.blob.size:file.size), packed=extract?file.size:data.blob.size;
        by('original').textContent=size(raw);by('packed').textContent=size(packed);
        by('saving').textContent=raw?fmt(Math.max(0,(1-packed/raw)*100))+' %':'—';
        view(data.entries?'Папка восстановлена':extract?'Файл восстановлен':'Архив готов',data.name+' · '+size(data.blob.size)+(extract?' · SHA-256 совпадает':'')+(filesOnly?' · Пустые каталоги доступны при перетаскивании папки.':''),
          data.entries?'Скачать папку в ZIP':extract?'Скачать файл':'Скачать архив','check','download',extract?'Распаковать ещё':'Выбрать другой файл','folder');
        folderButton.hidden=extract && !(data.entries && window.showDirectoryPicker);
        folderButton.textContent=extract?'Сохранить в каталог':'Выбрать папку';
      };
      worker.postMessage({file,extract,entries});
    }catch {error('Браузер не смог запустить локальную обработку.');}
  }
  main.onclick=()=>{
    if(phase==='working'||phase==='reading'){idle();return;}
    if(phase!=='done'){pick.click();return;}
    const a=document.createElement('a');a.href=downloadUrl;a.download=result.name;
    document.body.appendChild(a);a.click();a.remove();
  };
  pick.onchange=()=>run(pick.files[0]);
  async function collect(read,filesOnly=false){
    clear();const token=selection;phase='reading';folderButton.hidden=true;
    view('Читаем папку…','Собираем список файлов и каталогов.','Отменить','folder','x','Выбрать другой файл','folder');
    try{
      const entries=await read();
      if(token!==selection)return;
      run({name:entries[0].path,size:entries.reduce((sum,e)=>sum+(e.file?.size||0),0)},entries,filesOnly);
    }catch(e){if(token===selection)error(e.message);}
  }
  folderInput.onchange=()=>{const files=Array.from(folderInput.files);collect(()=>folderFromFiles(files),true);};
  folderButton.onclick=async()=>{
    const token=selection;
    if(extract && result?.entries){
      const tree=result.entries;
      try{
        const parent=await window.showDirectoryPicker({mode:'readwrite'});
        if(token!==selection)return;
        folderButton.disabled=true;
        const saved=await saveFolderEntries(parent,tree);
        if(token===selection)by('dropText').textContent='Сохранено: '+saved;
      }catch(e){
        if(e.name!=='AbortError' && token===selection)by('dropText').textContent='Для сохранения папки скачайте ZIP. '+(e.name==='SecurityError'?'':e.message);
      }finally{folderButton.disabled=false;}
      return;
    }
    if(window.showDirectoryPicker){
      try{
        const handle=await window.showDirectoryPicker();
        if(token===selection)await collect(()=>folderFromHandle(handle));
        return;
      }catch(e){if(e.name==='AbortError')return;if(e.name!=='SecurityError' && e.name!=='NotAllowedError'){error(e.message);return;}}
    }
    by('dropText').textContent='Чтобы включить пустые каталоги, перетащите папку сюда.';
    folderInput.value='';folderInput.click();
  };
  sample.onclick=()=>{
    if(phase==='done'||phase==='working'||phase==='reading'){pick.value='';pick.click();return;}
    if(extract){
      const bytes=Uint8Array.from(atob('/*SAMPLE*/'),c=>c.charCodeAt(0));
      run(new File([bytes],'пример.csv.skl'));return;
    }
    let csv='день;категория;значение\n';
    for(let i=0;i<1536;i++) csv+=(i%31+1)+';'+['книги','музыка','кино'][i%3]+';'+(i%12+1)+'\n';
    run(new File([csv],'пример.csv',{type:'text/csv'}));
  };
  by('compress').onclick=()=>{if(extract){extract=false;idle();}};
  by('decompress').onclick=()=>{if(!extract){extract=true;idle();}};
  drop.addEventListener('dragenter',e=>{e.preventDefault();drags++;drop.classList.add('drag');});
  drop.addEventListener('dragover',e=>{e.preventDefault();e.dataTransfer.dropEffect='copy';});
  drop.addEventListener('dragleave',()=>{if(--drags<=0){drags=0;drop.classList.remove('drag');}});
  drop.addEventListener('drop',e=>{
    e.preventDefault();drop.classList.remove('drag');drags=0;
    const items=[...e.dataTransfer.items].filter(item=>item.kind==='file');
    if(items.length===1){
      const entry=items[0].webkitGetAsEntry?.();
      if(entry?.isDirectory){
        if(extract){error('Для распаковки выберите архив .skl.');return;}
        collect(()=>folderFromDrop(entry));return;
      }
    }
    if(e.dataTransfer.files.length!==1){error('Выберите один файл или одну папку.');return;}
    run(e.dataTransfer.files[0]);
  });
  window.addEventListener('dragover',e=>{if(e.dataTransfer.types.includes('Files'))e.preventDefault();});
  window.addEventListener('drop',e=>{if(e.dataTransfer.types.includes('Files'))e.preventDefault();});
  const info=by('info');document.querySelector('.about').onclick=()=>info.showModal();
  document.querySelectorAll('.close,.closeText').forEach(b=>b.onclick=()=>info.close());
  info.onclick=e=>{if(e.target===info){const r=info.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)info.close();}};
  idle();
})();
