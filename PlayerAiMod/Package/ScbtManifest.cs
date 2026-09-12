using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>manifest.blackboard 里的一条黑板定义。</summary>
    public sealed class ScbtBlackboardEntry
    {
        public string Name;

        /// <summary>bool | int | float | string | actor（见 <see cref="BtSchema.BlackboardTypes"/>）。</summary>
        public string Type = "float";

        /// <summary>只读键（由服务/传感器写入，节点只读它）。</summary>
        public bool Readonly;

        public string Description;

        public override string ToString()
        {
            return Name + ":" + Type + (Readonly ? " (readonly)" : string.Empty);
        }
    }

    /// <summary>manifest.references 里的一条嵌套引用（id 供 Subtree 节点使用）。</summary>
    public sealed class ScbtReference
    {
        /// <summary>引用 id（包内唯一；Subtree 节点的 properties.package 写它）。</summary>
        public string Id;

        /// <summary>相对本包所在目录的路径（必须以 .scbtpak 结尾）。</summary>
        public string Path;

        public override string ToString()
        {
            return Id + " -> " + Path;
        }
    }

    /// <summary>
    /// `.scbtpak` 的 manifest.json（格式 v1，见 doc/player-ai-plan.md §4.1）。
    /// 一棵树一个包：这里写"这棵树叫什么、入口是哪个节点、有哪些黑板键、引用哪些别的包"。
    /// </summary>
    public sealed class ScbtManifest
    {
        public const string FormatName = "scbt";
        public const int FormatVersion = 1;
        public const string FileName = "manifest.json";

        /// <summary>原始 JSON（导出时以它为底，保留我们还不认识的字段）。</summary>
        public PackageValue Raw;

        public string Format = FormatName;

        public int Version = FormatVersion;

        public string Id;

        public string Name;

        /// <summary>入口节点 id；为空表示用 tree.json 的文档根。</summary>
        public string Entry;

        public readonly List<ScbtBlackboardEntry> Blackboard = new List<ScbtBlackboardEntry>();

        public readonly List<ScbtReference> References = new List<ScbtReference>();

        public bool TryGetReference(string id, out ScbtReference reference)
        {
            for (int i = 0; i < References.Count; i++)
            {
                if (string.Equals(References[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    reference = References[i];
                    return true;
                }
            }
            reference = null;
            return false;
        }

        /// <summary>读 manifest.json。只做"读数"（含类型不符上报），语义校验见 <see cref="Validate"/>。</summary>
        public static ScbtManifest Parse(PackageValue root, string where, PackageReport report)
        {
            if (root == null || !root.IsObject)
            {
                report.Error(PackageCodes.ManifestNotObject, where,
                    "manifest must be a JSON object, found " + (root == null ? "null" : root.DescribeKind()));
                return null;
            }

            var manifest = new ScbtManifest { Raw = root.DeepClone() };
            var reader = new PackageReader(root, where, report);

            manifest.Format = reader.Str("format", FormatName);
            manifest.Version = reader.Int("version", FormatVersion);
            manifest.Id = reader.Str("id", null);
            manifest.Name = reader.Str("name", null);
            manifest.Entry = reader.Str("entry", null);

            PackageValue blackboard = reader.ArrayField("blackboard");
            for (int i = 0; i < blackboard.Count; i++)
            {
                PackageValue item = blackboard.Item(i);
                string itemWhere = where + ".blackboard[" + i + "]";
                if (!item.IsObject)
                {
                    report.Error(PackageCodes.ManifestBlackboard, itemWhere,
                        "blackboard entry must be an object, found " + item.DescribeKind());
                    continue;
                }
                var itemReader = new PackageReader(item, itemWhere, report);
                var entry = new ScbtBlackboardEntry
                {
                    Name = itemReader.Str("name", null),
                    Type = itemReader.Enum("type", "float", BtSchema.BlackboardTypes),
                    Readonly = itemReader.Bool("readonly", false),
                    Description = itemReader.Str("description", null)
                };
                itemReader.ReportUnknown();
                manifest.Blackboard.Add(entry);
            }

            PackageValue references = reader.ArrayField("references");
            for (int i = 0; i < references.Count; i++)
            {
                PackageValue item = references.Item(i);
                string itemWhere = where + ".references[" + i + "]";
                if (!item.IsObject)
                {
                    report.Error(PackageCodes.ManifestReference, itemWhere,
                        "reference must be an object, found " + item.DescribeKind());
                    continue;
                }
                var itemReader = new PackageReader(item, itemWhere, report);
                var reference = new ScbtReference
                {
                    Id = itemReader.Str("id", null),
                    Path = itemReader.Str("path", null)
                };
                itemReader.ReportUnknown();
                manifest.References.Add(reference);
            }

            reader.ReportUnknown();
            return manifest;
        }

        /// <summary>
        /// 语义校验（计划 §4.1 的约束）。任何一条 Error 都意味着**这个包不能被装载**：
        /// 与其装进去跑出莫名其妙的行为，不如当场说清楚。
        /// </summary>
        public void Validate(string where, PackageReport report)
        {
            if (!string.Equals(Format, FormatName, StringComparison.OrdinalIgnoreCase))
            {
                report.Error(PackageCodes.ManifestFormat, where + ".format",
                    "format must be '" + FormatName + "', found '" + (Format ?? "<null>") + "'");
            }

            if (Version != FormatVersion)
            {
                report.Error(PackageCodes.ManifestVersion, where + ".version",
                    "unsupported format version " + Version + " (this build understands " + FormatVersion + ")");
            }

            if (!PackageJson.IsValidIdentifier(Id))
            {
                report.Error(PackageCodes.ManifestId, where + ".id",
                    "id must be 1-64 characters of [A-Za-z0-9._-], found '" + (Id ?? "<null>") + "'");
            }

            if (!string.IsNullOrEmpty(Entry) && !PackageJson.IsValidIdentifier(Entry))
            {
                report.Error(PackageCodes.ManifestEntry, where + ".entry",
                    "entry must be a node id ([A-Za-z0-9._-]), found '" + Entry + "'");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Blackboard.Count; i++)
            {
                ScbtBlackboardEntry entry = Blackboard[i];
                string itemWhere = where + ".blackboard[" + i + "]";
                if (!PackageJson.IsValidIdentifier(entry.Name))
                {
                    report.Error(PackageCodes.ManifestBlackboard, itemWhere + ".name",
                        "blackboard name must be 1-64 characters of [A-Za-z0-9._-], found '"
                        + (entry.Name ?? "<null>") + "'");
                }
                else if (!names.Add(entry.Name))
                {
                    report.Error(PackageCodes.ManifestBlackboard, itemWhere + ".name",
                        "duplicate blackboard key '" + entry.Name + "'");
                }
            }

            var referenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < References.Count; i++)
            {
                ScbtReference reference = References[i];
                string itemWhere = where + ".references[" + i + "]";
                if (!PackageJson.IsValidIdentifier(reference.Id))
                {
                    report.Error(PackageCodes.ManifestReference, itemWhere + ".id",
                        "reference id must be 1-64 characters of [A-Za-z0-9._-], found '"
                        + (reference.Id ?? "<null>") + "'");
                }
                else if (!referenceIds.Add(reference.Id))
                {
                    report.Error(PackageCodes.ManifestReferenceDuplicate, itemWhere + ".id",
                        "duplicate reference id '" + reference.Id + "'");
                }

                if (string.IsNullOrEmpty(reference.Path))
                {
                    report.Error(PackageCodes.ManifestReferencePath, itemWhere + ".path",
                        "reference path is required");
                    continue;
                }

                // 相对路径 + 限定扩展名：绝对路径与路径穿越都在这里拦掉（计划 §4.4 白名单）
                if (reference.Path.IndexOf(':') >= 0 || reference.Path.StartsWith("/", StringComparison.Ordinal)
                    || reference.Path.StartsWith("\\", StringComparison.Ordinal))
                {
                    report.Error(PackageCodes.ManifestReferencePath, itemWhere + ".path",
                        "reference path must be relative to the package, found '" + reference.Path + "'");
                }
                if (!reference.Path.EndsWith(".scbtpak", StringComparison.OrdinalIgnoreCase))
                {
                    report.Error(PackageCodes.ManifestReferencePath, itemWhere + ".path",
                        "reference path must end with .scbtpak, found '" + reference.Path + "'");
                }
                if (reference.Path.Contains(".."))
                {
                    report.Error(PackageCodes.ManifestReferencePath, itemWhere + ".path",
                        "reference path must not contain '..', found '" + reference.Path + "'");
                }
            }
        }

        /// <summary>写回 JSON（导出与编辑器保存用；未识别字段原样保留）。</summary>
        public PackageValue ToValue()
        {
            PackageValue value = Raw != null ? Raw.DeepClone() : PackageValue.Object();
            if (!value.IsObject)
                value = PackageValue.Object();

            value.Set("format", PackageValue.Str(FormatName));
            value.Set("version", PackageValue.Number(FormatVersion));
            value.Set("id", PackageValue.Str(Id));
            if (!string.IsNullOrEmpty(Name))
                value.Set("name", PackageValue.Str(Name));
            if (!string.IsNullOrEmpty(Entry))
                value.Set("entry", PackageValue.Str(Entry));

            if (Blackboard.Count > 0)
            {
                PackageValue blackboard = PackageValue.Array();
                for (int i = 0; i < Blackboard.Count; i++)
                {
                    ScbtBlackboardEntry entry = Blackboard[i];
                    PackageValue item = PackageValue.Object();
                    item.Set("name", PackageValue.Str(entry.Name));
                    item.Set("type", PackageValue.Str(entry.Type));
                    if (entry.Readonly)
                        item.Set("readonly", PackageValue.Bool(true));
                    if (!string.IsNullOrEmpty(entry.Description))
                        item.Set("description", PackageValue.Str(entry.Description));
                    blackboard.Add(item);
                }
                value.Set("blackboard", blackboard);
            }
            else
            {
                value.Remove("blackboard");
            }

            if (References.Count > 0)
            {
                PackageValue references = PackageValue.Array();
                for (int i = 0; i < References.Count; i++)
                {
                    PackageValue item = PackageValue.Object();
                    item.Set("id", PackageValue.Str(References[i].Id));
                    item.Set("path", PackageValue.Str(References[i].Path));
                    references.Add(item);
                }
                value.Set("references", references);
            }
            else
            {
                value.Remove("references");
            }

            return value;
        }

        public override string ToString()
        {
            return "manifest(" + (Id ?? "?") + " v" + Version + " entry=" + (Entry ?? "<root>")
                + " refs=" + References.Count + ")";
        }
    }
}
