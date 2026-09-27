// Kartan på /allt (Views/Omfattning.cshtml). Innehållet ligger i omfattning-data.js.
// d3 circle packing: varje funktion är en lika stor cirkel, så en cirkels storlek är
// antalet funktioner i den. Talet på sidan räknas ur datat och kan därför inte ljuga.
function toObj(a){if(typeof a==="string")return {name:a,desc:""};const o={name:a[0],desc:a[1]||""};if(a[2])o.children=a[2].map(toObj);return o}
const root=d3.hierarchy(toObj(window.OMFATTNING_DATA)).sum(d=>d.children?0:1).sort((a,b)=>b.value-a.value);
const S=1000;
d3.pack().size([S,S]).padding(d=>d.depth===0?14:d.depth===1?6:d.depth===2?3:1.5)(root);
root.descendants().forEach((d,i)=>{d.id=i;d.name=d.data.name;d.desc=d.data.desc;d.area=d.depth===0?null:(d.depth===1?d:d.ancestors().find(a=>a.depth===1))});
const areas=root.children;
areas.forEach((a,i)=>a.ci=i);
document.getElementById("nLeaf").textContent=root.leaves().length;
document.getElementById("nArea").textContent=areas.length;

const svg=d3.select("#svg"),stage=document.getElementById("stage");
const g=svg.append("g");
const nodes=root.descendants();
const circ=g.selectAll("circle").data(nodes).join("circle").attr("class","n")
  .attr("cx",d=>d.x).attr("cy",d=>d.y).attr("r",d=>d.r);
const labels=g.selectAll("text").data(nodes.filter(d=>d.depth>0)).join("text")
  .attr("class",d=>"l"+(d.children?" p":""));

let tokens={};
function readTokens(){const cs=getComputedStyle(document.documentElement);
  ["--root","--root-stroke","--surface","--ink"].forEach(k=>tokens[k]=cs.getPropertyValue(k).trim());
  tokens.area=[1,2,3,4,5,6,7,8,9,10,11].map(i=>cs.getPropertyValue("--a"+i).trim());}
function paint(){readTokens();
  circ.attr("fill",d=>d.depth===0?tokens["--root"]:tokens.area[d.area.ci])
    .attr("fill-opacity",d=>d.depth===0?1:d.children?(d.depth===1?.13:.17):.34)
    .attr("stroke",d=>d.depth===0?tokens["--root-stroke"]:tokens.area[d.area.ci])
    .attr("stroke-opacity",d=>d.depth===0?1:d.children?.55:.4)
    .attr("vector-effect","non-scaling-stroke")
    .attr("stroke-width",d=>d.depth<=1?1.5:1);
  renderInfo(focus);renderCrumbs(focus);}

let W=0,H=0,k=1,focus=root;
function wrap(name,cpl){const w=name.split(" "),lines=[];let cur="";
  for(const x of w){if(cur&&(cur+" "+x).length>cpl){lines.push(cur);cur=x}else cur=cur?cur+" "+x:x}
  if(cur)lines.push(cur);return lines}
function setLines(el,d,lines){const key=lines.join("|");if(d._key===key)return;d._key=key;
  el.selectAll("tspan").remove();
  lines.forEach((ln,i)=>el.append("tspan").attr("x",d.x).attr("dy",i===0?(-(lines.length-1)/2)+"em":"1.12em").text(ln));}

function layoutLabels(){
  labels.each(function(d){
    const el=d3.select(this),sr=d.r*k,psr=d.parent.r*k;
    const parentOpen=d.parent.depth===0||psr>120;
    let show=false,fs=12,y=d.y,lines=[d.name];
    if(!parentOpen){show=false}
    else if(d.children&&sr>120){ // öppen förälder: namn uppe i kanten
      fs=Math.min(15+ (d.depth===1?3:0),sr/9);show=true;y=d.y-d.r+(fs*1.35)/k;
      lines=[d.name];
    } else if(sr>(d.children?30:24)){
      fs=d.children?Math.max(11,Math.min(22,sr/4.2)):Math.max(9.5,Math.min(16,sr/4.6));
      const cpl=Math.max(6,Math.floor((sr*1.6)/(fs*.55)));
      lines=wrap(d.name,cpl);
      if(lines.length>3&&!d.children){fs=Math.max(9,fs*.85);lines=wrap(d.name,Math.floor((sr*1.7)/(fs*.55)))}
      show=lines.length<=4;
    }
    el.attr("display",show?null:"none");
    if(!show)return;
    el.attr("font-size",fs/k).attr("y",y).attr("stroke-width",3/k).attr("letter-spacing",d.children&&sr>120?(0.02*fs/k):null);
    setLines(el,d,lines);
    if(d.children&&sr>120)el.selectAll("tspan").attr("dy","0em");
  });
}

const zoom=d3.zoom().scaleExtent([.5,3000]).on("zoom",e=>{k=e.transform.k;g.attr("transform",e.transform);layoutLabels()});
svg.call(zoom).on("dblclick.zoom",null);

function fitTransform(d){const pad=Math.min(W,H)<520?.94:.86,r=d.children?d.r:d.r*2.4;
  const topSpace=W<760?64:0;
  const kk=Math.min(W,H-topSpace)*pad/(2*r);
  return d3.zoomIdentity.translate(W/2-d.x*kk,(H+topSpace)/2-d.y*kk).scale(kk)}
function goTo(d,animate=true){focus=d;
  const t=fitTransform(d);
  const reduce=matchMedia("(prefers-reduced-motion: reduce)").matches;
  (animate&&!reduce?svg.transition().duration(750).ease(d3.easeCubicInOut):svg).call(zoom.transform,t);
  renderInfo(d);renderCrumbs(d);}

circ.on("click",(e,d)=>{e.stopPropagation();goTo(d===focus&&d.parent?d.parent:d)});
svg.on("click",()=>{if(focus.parent)goTo(focus.parent)});

function color(d){return d.depth===0?tokens["--ink"]:tokens.area[d.area.ci]}
function nFunc(d){const n=d.children?d.leaves().length:0;return n?n+(n===1?" funktion":" funktioner"):""}
function renderInfo(d){
  const info=document.getElementById("info");info.innerHTML="";
  const eb=document.createElement("div");eb.className="eyebrow";
  const dot=document.createElement("span");dot.className="dot";dot.style.background=color(d);
  eb.append(dot,document.createTextNode(d.depth===0?"Hela plattformen":d.ancestors().slice(1).reverse().slice(0,-1).map(a=>a.name).join(" / ")||"Område"));
  const h=document.createElement("h2");h.textContent=d.name;
  info.append(eb,h);
  if(d.children){const c=document.createElement("div");c.className="count";
    c.textContent=d.depth===0?`${nFunc(d)} i ${d.children.length} områden`:`${nFunc(d)}${d.children.some(x=>x.children)?" i "+d.children.length+" delar":""}`;info.append(c)}
  if(d.desc){const p=document.createElement("p");p.textContent=d.desc;info.append(p)}
  if(d.depth===1&&d.parent){const share=Math.round(100*d.leaves().length/root.leaves().length);
    const p=document.createElement("p");p.className="count";p.textContent=`${share} % av allt som finns i pistol.nu.`;info.append(p)}
  if(d.children){const list=document.createElement("div");list.className="kids";
    d.children.forEach(ch=>{const b=document.createElement("button");
      const a=document.createElement("span");a.textContent=ch.name;
      const n=document.createElement("span");n.textContent=ch.children?ch.leaves().length:"";
      b.append(a,n);b.onclick=()=>goTo(ch);list.append(b)});info.append(list)}
  else if(d.parent){const b=document.createElement("button");b.className="chip";b.style.alignSelf="flex-start";
    b.textContent="Tillbaka till "+d.parent.name;b.onclick=()=>goTo(d.parent);info.append(b)}
  const hint=document.createElement("div");hint.className="hint";
  hint.textContent=d.depth===0?"Klicka på ett område för att gå in. Nyp, scrolla eller dra för att röra dig fritt. Klicka utanför cirkeln för att gå ett steg upp.":"Klicka utanför den markerade cirkeln för att gå ett steg upp.";
  info.append(hint);
}
function renderCrumbs(d){const c=document.getElementById("crumbs");c.innerHTML="";
  d.ancestors().reverse().forEach((a,i,arr)=>{if(i)c.append(Object.assign(document.createElement("span"),{className:"sep",textContent:"›"}));
    const b=document.createElement("button");b.className="chip";b.textContent=a.name;if(i===arr.length-1)b.style.borderColor=color(a);
    b.onclick=()=>goTo(a);c.append(b)});}

// sök
const q=document.getElementById("q"),hits=document.getElementById("hits");
const norm=s=>s.toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g,"");
nodes.forEach(d=>d._s=norm(d.name+" "+d.desc));
let sel=0,found=[];
function showHits(){const v=norm(q.value.trim());hits.innerHTML="";
  if(!v){hits.hidden=true;return}
  found=nodes.filter(d=>d.depth>0&&d._s.includes(v)).slice(0,12);sel=0;
  if(!found.length){hits.innerHTML='<div style="padding:10px 12px;color:var(--muted)">Ingen träff. Prova ett annat ord.</div>';hits.hidden=false;return}
  found.forEach((d,i)=>{const b=document.createElement("button");if(i===0)b.className="on";
    b.textContent=d.name;const s=document.createElement("small");s.textContent=d.ancestors().slice(1,-1).reverse().map(a=>a.name).join(" / ")||"Område";
    b.append(s);b.onclick=()=>pick(d);hits.append(b)});hits.hidden=false}
function pick(d){hits.hidden=true;q.value="";q.blur();goTo(d)}
q.addEventListener("input",showHits);
q.addEventListener("keydown",e=>{if(hits.hidden)return;const bs=[...hits.querySelectorAll("button")];
  if(e.key==="ArrowDown"||e.key==="ArrowUp"){e.preventDefault();sel=(sel+(e.key==="ArrowDown"?1:-1)+bs.length)%bs.length;bs.forEach((b,i)=>b.classList.toggle("on",i===sel))}
  else if(e.key==="Enter"&&found[sel]){e.preventDefault();pick(found[sel])}
  else if(e.key==="Escape"){hits.hidden=true}});
document.addEventListener("click",e=>{if(!e.target.closest(".search"))hits.hidden=true});

document.getElementById("zin").onclick=()=>svg.transition().duration(300).call(zoom.scaleBy,1.8);
document.getElementById("zout").onclick=()=>svg.transition().duration(300).call(zoom.scaleBy,1/1.8);
document.getElementById("zhome").onclick=()=>goTo(root);
// QR-koden. Rutan är en del av sidan, inte en webbläsardialog, och stängs med Escape,
// krysset eller ett klick utanför kortet.
const qrOverlay=document.getElementById("qrOverlay"),qrOpenBtn=document.getElementById("qrOpen");
function openQr(){qrOverlay.hidden=false;document.getElementById("qrCopied").textContent="";document.getElementById("qrClose").focus()}
function closeQr(){qrOverlay.hidden=true;qrOpenBtn.focus()}
qrOpenBtn.onclick=openQr;
document.getElementById("qrClose").onclick=closeQr;
qrOverlay.addEventListener("click",e=>{if(e.target===qrOverlay)closeQr()});
document.getElementById("qrCopy").onclick=async e=>{
  const url=e.currentTarget.dataset.url,out=document.getElementById("qrCopied");
  try{await navigator.clipboard.writeText(url);out.textContent="Länken är kopierad."}
  catch{ // Äldre webbläsare: markera adressen så att den går att kopiera för hand.
    const r=document.createRange();r.selectNodeContents(document.querySelector(".qr-url"));
    const s=getSelection();s.removeAllRanges();s.addRange(r);out.textContent="Markerad – kopiera med Ctrl+C."}
};

document.addEventListener("keydown",e=>{
  if(e.key!=="Escape")return;
  if(!qrOverlay.hidden){closeQr();return}
  if(focus.parent&&document.activeElement!==q)goTo(focus.parent)});

function resize(){const r=stage.getBoundingClientRect();W=r.width;H=r.height;svg.attr("viewBox",[0,0,W,H]);
  svg.call(zoom.transform,fitTransform(focus))}
new ResizeObserver(resize).observe(stage);
matchMedia("(prefers-color-scheme: dark)").addEventListener("change",paint);
new MutationObserver(paint).observe(document.documentElement,{attributes:true,attributeFilter:["data-theme"]});
resize();paint();
