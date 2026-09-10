using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ClaudeCodeWorkbench
{
    // Read package-local raster images only. Never resolve an OOXML relationship on disk/network.
    internal sealed class OfficeMedia
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, string> _images = new Dictionary<string, string>(StringComparer.Ordinal);
        private int _renderedBytes, _renderedImages;
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        internal OfficeMedia(ZipArchive zip, string part)
        {
            _zip = zip;
            var slash = part.LastIndexOf('/');
            var folder = slash < 0 ? "" : part.Substring(0, slash + 1);
            var entry = zip.GetEntry(folder + "_rels/" + part.Substring(slash + 1) + ".rels");
            if (entry == null) return;
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
            {
                foreach (var rel in XElement.Load(reader).Elements())
                {
                    if (!((string)rel.Attribute("Type") ?? "").EndsWith("/image", StringComparison.Ordinal) ||
                        string.Equals((string)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = (string)rel.Attribute("Id") ?? "";
                    var path = ResolvePart(folder, (string)rel.Attribute("Target") ?? "");
                    if (id.Length > 0 && path != null && !_images.ContainsKey(id)) _images.Add(id, path);
                }
            }
        }

        internal static string ResolvePart(string folder, string target)
        {
            if (string.IsNullOrWhiteSpace(target) || target.IndexOfAny(new[] { ':', '\\', '?', '#', '%', '\0' }) >= 0 || target.StartsWith("//", StringComparison.Ordinal)) return null;
            var segments = new List<string>();
            foreach (var segment in (target.StartsWith("/", StringComparison.Ordinal) ? target.Substring(1) : folder + target).Split('/'))
            {
                if (segment == "" || segment == ".") continue;
                if (segment == "..") { if (segments.Count == 0) return null; segments.RemoveAt(segments.Count - 1); }
                else segments.Add(segment);
            }
            return segments.Count == 0 ? null : string.Join("/", segments);
        }

        internal string Render(XElement container)
        {
            var output = new StringBuilder();
            foreach (var blip in container.Descendants(A + "blip").Take(101))
            {
                var properties = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "docPr" || e.Name.LocalName == "cNvPr");
                var alt = (string)properties?.Attribute("descr") ?? (string)properties?.Attribute("name") ?? "文档内嵌图片";
                alt = WebUtility.HtmlEncode(alt.Substring(0, Math.Min(alt.Length, 256)));
                string part;
                if (++_renderedImages > 100 || !_images.TryGetValue((string)blip.Attribute(R + "embed") ?? "", out part))
                { output.Append("<span class=\"media-placeholder\">[外部或未支持的图片]</span>"); continue; }
                var entry = _zip.GetEntry(part);
                if (entry == null || entry.Length > 1024 * 1024 || _renderedBytes + entry.Length > 3 * 1024 * 1024)
                { output.Append("<span class=\"media-placeholder\">[图片超出预览上限，请用系统应用查看]</span>"); continue; }
                byte[] bytes;
                using (var input = entry.Open()) using (var memory = new MemoryStream()) { input.CopyTo(memory); bytes = memory.ToArray(); }
                var mime = RasterType(bytes);
                if (mime == null) { output.Append("<span class=\"media-placeholder\">[仅内嵌预览 PNG / JPEG 图片]</span>"); continue; }
                _renderedBytes += bytes.Length;
                output.Append("<img class=\"office-image\" alt=\"").Append(alt).Append("\" src=\"data:").Append(mime)
                    .Append(";base64,").Append(Convert.ToBase64String(bytes)).Append("\">");
            }
            return output.ToString();
        }

        private static bool SafeDimensions(long width, long height) { return width > 0 && height > 0 && width <= 8192 && height <= 8192 && width * height <= 16000000; }
        internal static string RasterType(byte[] b)
        {
            if (b.Length >= 24 && b.Take(8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }) && Encoding.ASCII.GetString(b,12,4) == "IHDR")
            {
                Func<int, long> number = i => ((long)b[i] << 24) | ((long)b[i+1] << 16) | ((long)b[i+2] << 8) | b[i+3];
                return SafeDimensions(number(16), number(20)) ? "image/png" : null;
            }
            if (b.Length < 4 || b[0] != 255 || b[1] != 216) return null;
            for (var i = 2; i + 3 < b.Length;)
            {
                if (b[i++] != 255) return null;
                while (i < b.Length && b[i] == 255) i++;
                if (i + 2 >= b.Length) return null;
                var marker = b[i++];
                if (marker == 217 || marker == 218) return null;
                if (marker == 1 || marker >= 208 && marker <= 215) continue;
                var length = (b[i] << 8) | b[i+1];
                if (length < 2 || i + length > b.Length) return null;
                if (new[] {192,193,194,195,197,198,199,201,202,203,205,206,207}.Contains((int)marker))
                    return length >= 7 && SafeDimensions((b[i+5] << 8) | b[i+6], (b[i+3] << 8) | b[i+4]) ? "image/jpeg" : null;
                i += length;
            }
            return null;
        }
    }
}
