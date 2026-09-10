using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using ClaudeCodeWorkbench;

namespace ClaudeCodeWorkbench { internal static class ProviderStore { public static string NowIso(){return DateTimeOffset.UtcNow.ToString("o");} } }
class ProductGapsTest
{
    static int assertions;
    static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
    static void Zip(string path, params string[] parts){
        using(var archive=ZipFile.Open(path,ZipArchiveMode.Create))
            for(int i=0;i<parts.Length;i+=2)using(var writer=new StreamWriter(archive.CreateEntry(parts[i]).Open(),new UTF8Encoding(false)))writer.Write(parts[i+1]);
    }
    static int Main(){
        var root=Path.Combine(Path.GetTempPath(),"workbench-product-gaps-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            using(var rsa=new RSACryptoServiceProvider(2048)){
                rsa.PersistKeyInCsp=false;
                var manifest=JObject.Parse("{'schemaVersion':2,'type':'skill','id':'test','version':'1.0.0','minAppVersion':'0.7.0','permissions':['prompt-instructions'],'contentSha256':{},'signature':{'version':2,'algorithm':'RSA-SHA256','thumbprint':'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'}}");
                File.WriteAllText(Path.Combine(root,"SKILL.md"),"测试",new UTF8Encoding(false));
                using(var sha=SHA256.Create())manifest["contentSha256"]["SKILL.md"]=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(root,"SKILL.md")))).Replace("-","");
                manifest["signature"]["value"]=Convert.ToBase64String(rsa.SignData(ExtensionPackagePolicy.SigningBytes(manifest),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
                Check(ExtensionPackagePolicy.VerifySignature(manifest,rsa),"Signature roundtrip");
                ExtensionPackagePolicy.VerifyPayload(manifest,root);
                foreach(var key in new[]{"id","version","type","minAppVersion","maxAppVersion","development","permissions","contentSha256"}){
                    var changed=(JObject)manifest.DeepClone();changed[key]=key=="permissions"?(JToken)new JArray("bundled-scripts"):new JValue("tampered");
                    Check(!ExtensionPackagePolicy.VerifySignature(changed,rsa),"Unsigned metadata mutation: "+key);
                }
                var reordered=new JObject(manifest.Properties().Reverse().Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
                Check(ExtensionPackagePolicy.VerifySignature(reordered,rsa),"Canonical property ordering");
                var development=JObject.Parse("{'schemaVersion':2,'type':'skill','development':true,'minAppVersion':'0.7.0','permissions':['prompt-instructions']}");
                Check((string)ExtensionPackagePolicy.Validate(development,root,"6.4.23-dev.native.local",_=>null)["signatureStatus"]=="unsigned-development","Development labeled");
                foreach(var value in new[]{"nonsense","6","6.*","0.7.0;cmd"}){
                    var invalid=(JObject)development.DeepClone();invalid["minAppVersion"]=value;
                    Reject(()=>ExtensionPackagePolicy.Validate(invalid,root,"6.4.23",_=>null),"Invalid range rejected");
                }
                var elevated=(JObject)development.DeepClone();elevated["permissions"]=new JArray("prompt-instructions","process");
                Reject(()=>ExtensionPackagePolicy.Validate(elevated,root,"6.4.23",_=>null),"Self granted permissions");
                Directory.CreateDirectory(Path.Combine(root,"nested"));File.WriteAllText(Path.Combine(root,"nested","manifest.json"),"{}");
                Reject(()=>ExtensionPackagePolicy.VerifyPayload(manifest,root),"Nested manifest must be signed");
                var traversal=(JObject)manifest.DeepClone();traversal["contentSha256"]["../escape"]="".PadLeft(64,'a');
                Reject(()=>ExtensionPackagePolicy.VerifyPayload(traversal,root),"Traversal rejected");
                Reject(()=>ExtensionPackagePolicy.Validate(manifest,root,"6.4.23",_=>null),"Unknown publisher rejected");
            }
            var doc=Path.Combine(root,"中文 文件.docx");
            Zip(doc,"word/document.xml","<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>&lt;script&gt;中文&lt;/script&gt;</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>");
            var preview=DocumentPreview.Read(doc);var html=(string)preview["html"];
            Check(html.Contains("<strong>&lt;script&gt;中文&lt;/script&gt;</strong>")&&!html.Contains("<script>")&&html.Contains("<table>"),"Escaped DOCX table and bold");
            var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            Check(OfficeMedia.RasterType(png)=="image/png","PNG dimensions accepted");
            var hugePng=(byte[])png.Clone();hugePng[16]=127;
            Check(OfficeMedia.RasterType(hugePng)==null,"Oversized pixel dimensions blocked");
            Check(OfficeMedia.RasterType(Encoding.UTF8.GetBytes("<svg onload='alert(1)'/>"))==null,"SVG not raster");
            Check(OfficeMedia.ResolvePart("ppt/slides/","../media/image.png")=="ppt/media/image.png","Valid relative media");
            foreach(var target in new[]{"../../../escape.png","https://example.com/a.png","//example.com/a","..\\escape","%2e%2e/x"})
                Check(OfficeMedia.ResolvePart("word/",target)==null,"Unsafe media reference rejected");
            var illustrated=Path.Combine(root,"illustrated.docx");
            Zip(illustrated,"word/document.xml","<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><w:body><w:p><w:r><w:drawing><a:blip r:embed='image1'/><a:blip r:embed='external'/></w:drawing></w:r></w:p></w:body></w:document>",
                "word/_rels/document.xml.rels","<Relationships><Relationship Id='image1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' Target='media/picture.png'/><Relationship Id='external' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image' TargetMode='External' Target='https://example.com/private.png'/></Relationships>");
            using(var archive=ZipFile.Open(illustrated,ZipArchiveMode.Update))using(var stream=archive.CreateEntry("word/media/picture.png").Open())stream.Write(png,0,png.Length);
            html=(string)DocumentPreview.Read(illustrated)["html"];
            Check(html.Contains("data:image/png;base64,")&&html.Contains("img-src data:")&&!html.Contains("https://example.com"),"Embedded image and blocked external relationship");
            var config=Path.Combine(root,"runtime.mcp.json");
            File.WriteAllText(config,"{\"mcpServers\":{\"fixture\":{\"command\":\"fixture.exe\",\"args\":[\"--test\"],\"env\":{\"TIMEOUT\":\"600\"}}}}");
            Check((int)McpRuntimePolicy.ValidateTrustedConfigs(new JArray(config),root)["serverCount"]==1,"String environment accepted");
            File.WriteAllText(config,"{\"mcpServers\":{\"fixture\":{\"command\":\"fixture.exe\",\"env\":{\"TIMEOUT\":600}}}}");
            Reject(()=>McpRuntimePolicy.ValidateTrustedConfigs(new JArray(config),root),"Numeric environment fails before model billing");
            File.WriteAllText(config,"{\"mcpServers\":{\"fixture\":{\"command\":\"fixture.exe\",\"args\":[true]}}}");
            Reject(()=>McpRuntimePolicy.ValidateTrustedConfigs(new JArray(config),root),"Nonstring MCP arguments rejected");
            var malicious=Path.Combine(root,"external.docx");
            Zip(malicious,"word/document.xml","<!DOCTYPE doc [<!ENTITY leak SYSTEM 'file:///C:/Windows/win.ini'>]><doc>&leak;</doc>");
            Reject(()=>DocumentPreview.Read(malicious),"DTD prohibited");
            var sheet=Path.Combine(root,"table.xlsx");
            Zip(sheet,"xl/workbook.xml","<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='预算' r:id='r1'/></sheets></workbook>",
                "xl/_rels/workbook.xml.rels","<Relationships><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>",
                "xl/worksheets/sheet1.xml","<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1'><v>10</v></c><c r='C1'><v>20</v></c></row></sheetData></worksheet>");
            html=(string)DocumentPreview.Read(sheet)["html"];Check(html.Contains("预算")&&html.Contains("<td></td>")&&html.Contains("20"),"Spreadsheet sparse columns");
            var slides=Path.Combine(root,"slides.pptx");Zip(slides,"ppt/slides/slide1.xml","<slide xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>演示文字</a:t></a:r></a:p></slide>");
            Check(((string)DocumentPreview.Read(slides)["html"]).Contains("演示文字"),"Slide text");
            var huge=Path.Combine(root,"huge.docx");Zip(huge,"word/document.xml",new string('x',9*1024*1024));
            Reject(()=>DocumentPreview.Read(huge),"Zip expansion bound");
            var graph=WorkflowDag.Create(JObject.Parse("{'nodes':[{'id':'a','parentRunId':'parent1','prompt':'分析'},{'id':'b','parentRunId':'parent2','prompt':'总结','dependencies':['a']}]}"));
            Check(HttpQuery.Get("?path=C%3A%2F%E4%B8%AD%E6%96%87+file.docx","path")=="C:/中文 file.docx","UTF-8 query path");
            Check(HttpQuery.Get("?path=%252F","path")=="%2F","Query decoded exactly once");
            Reject(()=>HttpQuery.Get("?path=a&path=b","path"),"Ambiguous query rejected");
            Check(WorkflowDag.Ready(graph).Count()==1,"Cross parent dependency waits");
            graph["nodes"][0]["state"]="completed";Check((string)WorkflowDag.Ready(graph).Single()["id"]=="b","Cross parent ready");
            graph["paused"]=true;Check(!WorkflowDag.Ready(graph).Any(),"Pause stops dispatch");
            graph["paused"]=false;graph["nodes"][0]["state"]="failed";WorkflowDag.Propagate(graph);
            Check((string)graph["nodes"][1]["state"]=="blocked"&&(string)graph["state"]=="failed","Failure propagated");
            Reject(()=>WorkflowDag.Create(JObject.Parse("{'nodes':[{'id':'a','parentRunId':'p','prompt':'x','dependencies':['b']},{'id':'b','parentRunId':'p','prompt':'x','dependencies':['a']}]}")),"Cycle rejected");
            Reject(()=>WorkflowDag.Create(JObject.Parse("{'nodes':[{'id':'a','parentRunId':'p','prompt':'x','dependencies':['missing']}]}")),"Unknown edge rejected");
            Console.WriteLine("PASS product gap boundaries: "+assertions+" assertions");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
