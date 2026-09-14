using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class DocumentPreview
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static string Escape(string value) { return WebUtility.HtmlEncode(value ?? ""); }

        public static JObject Read(string path)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("文档不存在或已移动");
            if (new FileInfo(path).Length > 24L * 1024 * 1024) throw new InvalidOperationException("内嵌预览最多支持 24 MB；请使用系统应用打开");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pdf") return new JObject { ["kind"] = "pdf", ["name"] = Path.GetFileName(path), ["notice"] = "PDF 使用内置阅读器；不修改原文件。" };
            if (!new[] { ".docx", ".xlsx", ".pptx" }.Contains(extension))
                throw new InvalidOperationException("此格式暂不支持内嵌预览，请使用系统应用打开（支持 PDF、DOCX、XLSX、PPTX）");
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 64L * 1024 * 1024 ||
                    zip.Entries.Any(e => e.Length > 8L * 1024 * 1024))
                    throw new InvalidOperationException("文档解压大小超出安全预览上限");
                var content = extension == ".docx" ? Word(zip) : extension == ".xlsx" ? Excel(zip) : Slides(zip);
                return new JObject { ["kind"] = "office", ["name"] = Path.GetFileName(path), ["html"] = Wrap(content),
                    ["notice"] = "只读结构化预览：支持段落、表格、工作表、幻灯片及 DOCX/PPTX 内嵌 PNG/JPEG。图片每张最多 1 MB；每份 DOCX 或每页 PPTX 最多 100 张 / 3 MB。复杂图表、公式重算和精确分页请用系统应用查看；宏和外部链接不会执行。" };
            }
        }

        private static XElement Xml(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return new XElement("missing");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, settings)) return XElement.Load(reader);
        }

        private static string Word(ZipArchive zip)
        {
            var body = Xml(zip, "word/document.xml").Element(W + "body");
            if (body == null) throw new InvalidOperationException("DOCX 正文缺失");
            var media = new OfficeMedia(zip, "word/document.xml");
            var output = new StringBuilder();
            foreach (var element in body.Elements().Take(3000))
            {
                if (element.Name == W + "p") output.Append(Paragraph(element, media));
                else if (element.Name == W + "tbl")
                {
                    output.Append("<table>");
                    foreach (var row in element.Elements(W + "tr").Take(300))
                    {
                        output.Append("<tr>");
                        foreach (var cell in row.Elements(W + "tc").Take(64))
                            output.Append("<td>").Append(string.Concat(cell.Elements(W + "p").Select(p => Paragraph(p, media)))).Append("</td>");
                        output.Append("</tr>");
                    }
                    output.Append("</table>");
                }
                if (output.Length > 6000000) throw new InvalidOperationException("文档排版内容过大");
            }
            if (body.Elements().Count() > 3000) output.Append("<p>预览已截断到前 3000 个段落/表格。</p>");
            return output.ToString();
        }

        private static string Paragraph(XElement paragraph, OfficeMedia media)
        {
            var style = (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
            var tag = style == "Heading1" ? "h1" : style == "Heading2" ? "h2" : style == "Heading3" ? "h3" : "p";
            var align = (string)paragraph.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val");
            var css = align == "center" || align == "right" ? " style=\"text-align:" + align + "\"" : "";
            var output = new StringBuilder("<" + tag + css + ">");
            foreach (var run in paragraph.Descendants(W + "r"))
            {
                var text = string.Concat(run.Elements().Select(e => e.Name == W + "t" ? Escape(e.Value) : e.Name == W + "br" ? ((string)e.Attribute(W + "type") == "page" ? "<br class=\"page-break\">" : "<br>") : e.Name == W + "tab" ? "&#8195;" : ""));
                var props = run.Element(W + "rPr");
                if (props?.Element(W + "b") != null) text = "<strong>" + text + "</strong>";
                if (props?.Element(W + "i") != null) text = "<em>" + text + "</em>";
                if (props?.Element(W + "u") != null) text = "<u>" + text + "</u>";
                output.Append(text).Append(media.Render(run));
            }
            return output.Append("</" + tag + ">").ToString();
        }

        private static string Excel(ZipArchive zip)
        {
            var shared = Xml(zip, "xl/sharedStrings.xml").Elements(S + "si").Take(100000)
                .Select(e => string.Concat(e.Descendants(S + "t").Select(t => t.Value))).ToArray();
            var workbook = Xml(zip, "xl/workbook.xml");
            var relationships = Xml(zip, "xl/_rels/workbook.xml.rels").Elements().ToDictionary(e => (string)e.Attribute("Id") ?? "", e => (string)e.Attribute("Target") ?? "");
            var output = new StringBuilder();
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            foreach (var sheet in workbook.Descendants(S + "sheet").Take(30))
            {
                string target;
                if (!relationships.TryGetValue((string)sheet.Attribute(rel + "id") ?? "", out target)) continue;
                // No external relationship or traversal is ever fetched.
                if (target.Contains("..") || target.Contains(":") || target.Contains("\\")) continue;
                var part = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                output.Append("<section><h2>").Append(Escape((string)sheet.Attribute("name"))).Append("</h2><table>");
                var rows = Xml(zip, part).Descendants(S + "row").ToArray();
                foreach (var row in rows.Take(200))
                {
                    output.Append("<tr><th>").Append(Escape((string)row.Attribute("r"))).Append("</th>");
                    var position = 0;
                    foreach (var cell in row.Elements(S + "c").Take(100))
                    {
                        var address = (string)cell.Attribute("r") ?? "";
                        var column = 0;
                        foreach (var c in address.TakeWhile(c => c >= 'A' && c <= 'Z')) { column = column * 26 + c - 'A' + 1; if (column > 100) break; }
                        if (column > 100) break;
                        while (++position < column) output.Append("<td></td>");
                        var value = (string)cell.Element(S + "v") ?? string.Concat(cell.Descendants(S + "t").Select(e => e.Value));
                        int index;
                        if ((string)cell.Attribute("t") == "s" && int.TryParse(value, out index)) value = index >= 0 && index < shared.Length ? shared[index] : "";
                        output.Append("<td title=\"").Append(Escape(address)).Append("\">").Append(Escape(value)).Append("</td>");
                    }
                    output.Append("</tr>");
                }
                output.Append("</table><p>每张工作表预览前 200 行、100 列；公式显示文件保存的缓存值。</p></section>");
                if (output.Length > 1500000) throw new InvalidOperationException("工作表预览内容过大");
            }
            return output.ToString();
        }

        private static string Slides(ZipArchive zip)
        {
            var output = new StringBuilder(); var index = 0;
            foreach (var entry in zip.Entries.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                .OrderBy(e => int.Parse(System.Text.RegularExpressions.Regex.Match(e.FullName, @"slide(\d+)\.xml$").Groups[1].Value)).Take(100))
            {
                output.Append("<section class=\"slide\"><h2>幻灯片 ").Append(++index).Append("</h2>");
                var slide = Xml(zip, entry.FullName);
                foreach (var paragraph in slide.Descendants(A + "p"))
                    output.Append("<p>").Append(Escape(string.Concat(paragraph.Descendants(A + "t").Select(e => e.Value)))).Append("</p>");
                output.Append(new OfficeMedia(zip, entry.FullName).Render(slide)).Append("</section>");
                if (output.Length > 6000000) throw new InvalidOperationException("幻灯片预览内容过大");
            }
            return output.ToString();
        }

        private static string Wrap(string content)
        {
            return "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'\"><style>" +
                ".office-image{display:block;max-width:100%;height:auto;margin:16px 0}.media-placeholder{font-size:13px;color:#657184}.page-break{display:block;border-top:1px dashed #bbc1ca;margin-top:28px;break-after:page}" +
                "body{font:16px/1.7 system-ui,sans-serif;color:#242424;background:#e9ebef;margin:0;padding:24px}main{max-width:960px;background:white;margin:auto;padding:40px;overflow-wrap:anywhere}table{border-collapse:collapse;max-width:100%;font-size:14px}td,th{border:1px solid #cbd0d8;padding:6px 10px;min-width:35px}p{white-space:pre-wrap}section{overflow:auto;margin-bottom:24px}.slide{padding:30px;border:1px solid #bcc3ce;min-height:280px}h1,h2,h3{line-height:1.3}</style><main>" + content + "</main></html>";
        }
    }
}
