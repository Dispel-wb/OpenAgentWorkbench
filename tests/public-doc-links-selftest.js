const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(process.argv[2]||path.join(__dirname,'..'));
const files=['README.md',...fs.readdirSync(path.join(root,'docs')).filter(n=>n.endsWith('.md')).map(n=>'docs/'+n)];
const failures=[];
let checked=0;
for(const file of files){
  const text=fs.readFileSync(path.join(root,file),'utf8');
  for(const match of text.matchAll(/\]\(([^)]+)\)/g)){
    const target=match[1].replace(/^<|>$/g,'').split('#')[0];
    if(!target||/^[a-z][a-z\d+.-]*:/i.test(target))continue;
    checked++;
    if(!fs.existsSync(path.resolve(root,path.dirname(file),decodeURIComponent(target))))failures.push(`${file}: ${target}`);
  }
}
if(failures.length)throw new Error('Broken documentation links:\n'+failures.join('\n'));
console.log(JSON.stringify({publicDocLinks:'PASS',files:files.length,checked}));
