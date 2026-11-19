(() => {
  const canvas=document.querySelector('#art'),ctx=canvas.getContext('2d');
  const reduced=matchMedia('(prefers-reduced-motion: reduce)');
  let state='idle',spread=1,target=1,frame=0,last=0;
  const layers=[
    ['#24482e','#1d3525','#59754a','#486339','#89a66a'],
    ['#426e41','#294731','#799661','#557b48','#a5c484'],
    ['#658f50','#34573c','#93b772','#689050','#c2df99'],
    ['#89ac67','#487048','#a7cd80','#83a959','#daf2af'],
    ['#d2e8a9','#83b774','#c2df99','#94bd70','#efffcf']
  ];
  function render(now=0) {
    frame=0;
    const w=canvas.clientWidth,h=canvas.clientHeight;
    if(!w || !h) return;
    const dpr=Math.min(devicePixelRatio || 1,2);
    if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){
      canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);
    }
    const dt=Math.min(60,Math.max(1,now-last)); last=now;
    spread+= (target-spread)*(reduced.matches?1:1-Math.exp(-dt/125));
    ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
    const s=Math.min(w/460,h/267);
    ctx.translate(w/2,h*.55);ctx.scale(s,s);
    // Transform the square sheet onto an isometric plane.
    const ax=.91, ay=.37, bx=-.77, by=.45;
    const point=(x,y,z)=>[ax*x+bx*y,ay*x+by*y-z];
    function segment(a,b,color,width=1) {
      ctx.beginPath();ctx.moveTo(...a);ctx.lineTo(...b);
      ctx.strokeStyle=color;ctx.lineWidth=width;ctx.stroke();
    }
    function polygon(points,fill,edge,line=1){
      ctx.beginPath();points.forEach((p,i)=>i?ctx.lineTo(...p):ctx.moveTo(...p));ctx.closePath();
      ctx.fillStyle=fill;ctx.fill();
      if(edge){ctx.strokeStyle=edge;ctx.lineWidth=line;ctx.stroke();}
    }
    // Etched ground grid and registration marks.
    for(let i=-480;i<=480;i+=24){
      segment(point(i,-420,-62),point(i,420,-62),'#799b6b18',.7);
      segment(point(-420,i,-62),point(420,i,-62),'#799b6b18',.7);
    }
    for(const [x,y] of [[-180,-68],[173,-68],[-180,75],[173,75]]){
      segment([x-3.5,y],[x+3.5,y],'#a9c79775',.9);
      segment([x,y-3.5],[x,y+3.5],'#a9c79775',.9);
    }
    ctx.save();ctx.translate(0,53);ctx.scale(1,.32);
    const shadow=ctx.createRadialGradient(0,0,12,0,0,148);
    shadow.addColorStop(0,'#06160fdb');shadow.addColorStop(1,'#06160f00');
    ctx.fillStyle=shadow;ctx.fillRect(-150,-150,300,300);ctx.restore();
    const size=77,bevel=6;
    const corners=[[-size+bevel,-size],[size-bevel,-size],[size,-size+bevel],[size,size-bevel],[size-bevel,size],[-size+bevel,size],[-size,size-bevel],[-size,-size+bevel]];
    const zBase=-22, gap=8+12*spread;
    for(let k=0;k<layers.length;k++){
      const z=zBase+k*gap,c=layers[k],depth=5.3;
      const top=corners.map(([x,y])=>point(x,y,z)),bottom=corners.map(([x,y])=>point(x,y,z-depth));
      for(let j=2;j<7;j++){
        const m=(j+1)%8;
        polygon([top[j],top[m],bottom[m],bottom[j]],j<4?c[3]:c[2],c[1],.7);
        segment(bottom[j],bottom[m],c[4]+'95',.8);
      }
      const face=ctx.createLinearGradient(-105,-z-45,105,-z+56);
      face.addColorStop(0,c[1]);face.addColorStop(.42,c[0]);face.addColorStop(1,c[2]);
      polygon(top,face,c[4],1.25);
      ctx.save();ctx.transform(ax,ay,bx,by,0,-z);
      ctx.strokeStyle=c[4]+'65';ctx.lineWidth=.7;ctx.strokeRect(-70,-70,140,140);
      for(let i=-63;i<=62;i+=6)for(let j=-63;j<=62;j+=6){
        ctx.fillStyle=c[4]+(k===4?'58':'30');ctx.fillRect(i,j,.75,.75);
      }
      if(k===4) {
        ctx.fillStyle='#41653e';ctx.textAlign='left';ctx.font='bold 30px Consolas, monospace';
        ctx.fillText('.skl',-51,12);
        for(let j=0;j<19;j++) {
          const width=j%4===0?2.4:.85;
          ctx.fillRect(-52+j*3.1,27,width,6);
        }
        ctx.strokeStyle='#426e4485';ctx.lineWidth=1;
        for(let i=0;i<3;i++){ctx.beginPath();ctx.moveTo(36+i*5,-49);ctx.lineTo(42+i*5,-49);ctx.lineTo(42+i*5,-43);ctx.stroke();}
        ctx.fillStyle='#f5ffdcaa';ctx.fillRect(-63,-62,14,1.6);
      }
      ctx.restore();
    }
    if(Math.abs(target-spread)>.002) frame=requestAnimationFrame(render);
  }
  window.setArtState=(next,extract=false,progress=0)=>{
    state=next;
    target=state==='working'?(extract?.15+.85*progress/100:1-.85*progress/100):state==='done'?(extract?1.15:.15):1;
    if(!frame)frame=requestAnimationFrame(render);
  };
  new ResizeObserver(()=>{if(!frame)frame=requestAnimationFrame(render);}).observe(canvas);
})();
