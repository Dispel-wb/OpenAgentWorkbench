const fs=require('fs'),path=require('path'),assert=require('assert/strict'),{chromium}=require('playwright');
const base=process.env.CLAUDE_UI_BASE_URL,root=process.env.CLAUDE_UI_WORKSPACE,out=process.env.CLAUDE_UI_OUTPUT_DIR;
function crc32(data){let crc=0xffffffff;for(const b of data){crc^=b;for(let i=0;i<8;i++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}return(crc^0xffffffff)>>>0;}
function zipOne(name,content){
  const parts=Array.isArray(name)?name:[[name,content]],files=[],directory=[];let offset=0;
  for(const [part,value] of parts){
    const n=Buffer.from(part),b=Buffer.from(value),h=Buffer.alloc(30),c=Buffer.alloc(46),crc=crc32(b);
    h.writeUInt32LE(0x04034b50);h.writeUInt16LE(20,4);h.writeUInt32LE(crc,14);h.writeUInt32LE(b.length,18);h.writeUInt32LE(b.length,22);h.writeUInt16LE(n.length,26);
    c.writeUInt32LE(0x02014b50);c.writeUInt16LE(20,4);c.writeUInt16LE(20,6);c.writeUInt32LE(crc,16);c.writeUInt32LE(b.length,20);c.writeUInt32LE(b.length,24);c.writeUInt16LE(n.length,28);c.writeUInt32LE(offset,42);
    files.push(h,n,b);directory.push(c,n);offset+=h.length+n.length+b.length;
  }
  const table=Buffer.concat(directory),e=Buffer.alloc(22);
  e.writeUInt32LE(0x06054b50);e.writeUInt16LE(parts.length,8);e.writeUInt16LE(parts.length,10);e.writeUInt32LE(table.length,12);e.writeUInt32LE(offset,16);
  return Buffer.concat([...files,table,e]);
}
async function main(){
  const doc=path.join(root,'中文 排版预览.docx');
  const pdf=path.join(root,'中文 PDF.pdf');
  const stream='BT /F1 24 Tf 72 720 Td (PDF preview fixture) Tj ET';
  const objects=['<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Kids [3 0 R] /Count 1 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>','<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>','<< /Length '+stream.length+' >>\nstream\n'+stream+'\nendstream'];
  let pdfBody='%PDF-1.4\n',offsets=[0];
  objects.forEach((o,i)=>{offsets.push(Buffer.byteLength(pdfBody));pdfBody+=(i+1)+' 0 obj\n'+o+'\nendobj\n';});
  const xref=Buffer.byteLength(pdfBody);pdfBody+='xref\n0 6\n0000000000 65535 f \n'+offsets.slice(1).map(n=>String(n).padStart(10,'0')+' 00000 n \n').join('')+'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n'+xref+'\n%%EOF\n';
  fs.writeFileSync(pdf,pdfBody);
  fs.writeFileSync(doc,zipOne('word/document.xml',"<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:pPr><w:pStyle w:val='Heading1'/></w:pPr><w:r><w:t>排版验收</w:t></w:r></w:p><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>默认中文语境</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>单元格</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"));
  const illustrated=path.join(root,'图片预览.pptx');
  const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=','base64');
  fs.writeFileSync(illustrated,zipOne([
    ['ppt/slides/slide1.xml',"<slide xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><a:p><a:r><a:t>内嵌图片安全验收</a:t></a:r></a:p><a:blip r:embed='p1'/><a:blip r:embed='external'/></slide>"],
    ['ppt/slides/_rels/slide1.xml.rels',"<Relationships><Relationship Id='p1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='../media/picture.png'/><Relationship Id='external' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' TargetMode='External' Target='https://example.invalid/must-not-fetch.png'/></Relationships>"],
    ['ppt/media/picture.png',png]
  ]));
  const browser=await chromium.launch({headless:true,executablePath:process.env.CLAUDE_UI_BROWSER_EXE});
  try{
    const context=await browser.newContext({viewport:{width:1280,height:850},extraHTTPHeaders:{'X-Desktop-Secret':process.env.CLAUDE_UI_SECRET,'X-Workbench-Protocol':'2'}});
    const api=context.request,now=new Date().toISOString(),id='document-ui';
    const preview=await api.get(base+'/api/files/document?path='+encodeURIComponent(doc));
    assert.equal(preview.status(),200,await preview.text());
    await api.post(base+'/api/settings',{data:{workspace:root,theme:'dark',skin:'codex'}});
    await api.post(base+'/api/sessions',{data:[{id,claudeSessionId:id,title:'预览验收',workspace:root,createdAt:now,updatedAt:now,queue:[]}]});
    await api.post(base+'/api/sessions/'+id+'/messages',{data:[{id:'doc-msg',role:'assistant',text:'[文档预览]('+doc.replace(/\\/g,'/')+')\n\n[PDF]('+pdf.replace(/\\/g,'/')+')\n\n[图片预览]('+illustrated.replace(/\\/g,'/')+')',time:now}]});
    const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base);await page.locator('[data-local-path]').first().click();
    const dialog=page.getByRole('dialog',{name:'中文 排版预览.docx'});await dialog.waitFor();
    const frame=page.frameLocator('iframe[title="Office 文档内容"]');
    await frame.getByRole('heading',{name:'排版验收'}).waitFor({timeout:8000}).catch(async error=>{fs.mkdirSync(out,{recursive:true});await page.screenshot({path:path.join(out,'document-preview-failed.png')});throw new Error(error.message+' dialog='+await dialog.innerText());});
    assert.equal(await frame.locator('strong').innerText(),'默认中文语境');
    assert.equal(await frame.locator('td').innerText(),'单元格');
    assert.equal(await page.locator('iframe[title="Office 文档内容"]').getAttribute('sandbox'),'');
    assert(await page.locator('.app-shell').evaluate(n=>n.inert),'Background must be inert');
    const ax=await context.newCDPSession(page);const tree=await ax.send('Accessibility.getFullAXTree');
    assert(tree.nodes.some(n=>!n.ignored&&n.role?.value==='dialog'&&n.name?.value==='中文 排版预览.docx'));
    await page.getByRole('button',{name:'关闭文档预览'}).focus();
    await page.keyboard.press('Tab');
    assert(await page.evaluate(()=>document.activeElement.closest('[role=dialog]')!==null),'Tab escaped modal');
    fs.mkdirSync(out,{recursive:true});await page.screenshot({path:path.join(out,'document-preview-dark.png')});
    await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});
    assert(!(await page.locator('.app-shell').evaluate(n=>n.inert)),'Background remained inert');
    await page.locator('[data-local-path]').nth(1).click();
    await page.locator('iframe[title="PDF 阅读器"]').waitFor();
    assert((await page.locator('iframe[title="PDF 阅读器"]').getAttribute('src')).startsWith('blob:'),'PDF should use an authenticated blob, not expose a file URL/token');
    await page.getByRole('button',{name:'关闭文档预览'}).click();
    let externalImageRequests=0;page.on('request',r=>{if(r.url().includes('example.invalid'))externalImageRequests++;});
    await page.locator('[data-local-path]').nth(2).click();
    const imageFrame=page.frameLocator('iframe[title="Office 文档内容"]');
    await imageFrame.getByText('内嵌图片安全验收').waitFor();
    const embedded=imageFrame.locator('img');await embedded.waitFor();
    assert(await embedded.evaluate(n=>n.complete&&n.naturalWidth===1),'Embedded PNG must actually decode');
    assert.equal(await imageFrame.locator('.media-placeholder').count(),1);
    assert.equal(externalImageRequests,0,'External OOXML images must never be requested');
    await page.screenshot({path:path.join(out,'embedded-image-preview.png')});
    await page.getByRole('button',{name:'关闭文档预览'}).click();
    await page.getByRole('button',{name:'开发工作台'}).click();
    await page.locator('.inspector header').getByRole('button',{name:'运行',exact:true}).click();
    await page.getByRole('button',{name:'DAG 编排'}).click();
    await page.getByRole('dialog',{name:'多 Agent 工作流'}).waitFor();
    await page.getByRole('button',{name:'添加节点'}).click();
    assert.equal(await page.locator('.workflow-dialog fieldset').count(),2);
    await page.screenshot({path:path.join(out,'workflow-editor-dark.png')});
    assert.equal(errors.length,0,errors.join('\n'));
    console.log(JSON.stringify({documentPreviewUi:'PASS',sandbox:true,headingTableBold:true,focusTrap:true,accessibleDialog:true,workflowEditor:true}));
  }finally{await browser.close();}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
