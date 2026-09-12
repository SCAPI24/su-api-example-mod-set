using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>校验问题的严重程度。</summary>
    public enum PackageIssueSeverity
    {
        /// <summary>提示（不拦装载，例如"属性名不认识，可能拼错了"）。</summary>
        Info,

        /// <summary>可疑（能装载，但八成不是作者想要的）。</summary>
        Warning,

        /// <summary>错误（**禁止装载**：装载了也跑不起来或行为不可预期）。</summary>
        Error
    }

    /// <summary>
    /// 一条校验问题。Code 是稳定标识（控制面/编辑器按它做提示与统计，不要靠中文文案匹配）。
    /// </summary>
    public sealed class PackageIssue
    {
        public PackageIssue(PackageIssueSeverity severity, string code, string where, string message)
        {
            Severity = severity;
            Code = code ?? "issue";
            Where = where ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public PackageIssueSeverity Severity { get; }

        public string Code { get; }

        /// <summary>位置：包内路径 + 节点 id，例如 "tree.json#seq/t1"。</summary>
        public string Where { get; }

        public string Message { get; }

        public string Describe()
        {
            string prefix = Severity == PackageIssueSeverity.Error ? "ERROR"
                : Severity == PackageIssueSeverity.Warning ? "WARN" : "INFO";
            var builder = new StringBuilder();
            builder.Append(prefix).Append(' ').Append(Code);
            if (Where.Length > 0)
                builder.Append(" @").Append(Where);
            builder.Append(": ").Append(Message);
            return builder.ToString();
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>稳定的问题代码（新增时只追加，不改旧值：控制面与编辑器会按它做判断）。</summary>
    public static class PackageCodes
    {
        // 读取与解析
        public const string FileMissing = "file.missing";
        public const string FileTooLarge = "file.tooLarge";
        public const string FileUnreadable = "file.unreadable";
        public const string ZipInvalid = "zip.invalid";
        public const string ZipEntryMissing = "zip.entryMissing";
        public const string JsonInvalid = "json.invalid";
        public const string JsonTooDeep = "json.tooDeep";

        // manifest
        public const string ManifestMissing = "manifest.missing";
        public const string ManifestNotObject = "manifest.notObject";
        public const string ManifestFormat = "manifest.format";
        public const string ManifestVersion = "manifest.version";
        public const string ManifestId = "manifest.id";
        public const string ManifestIdDuplicate = "manifest.idDuplicate";
        public const string ManifestEntry = "manifest.entry";
        public const string ManifestBlackboard = "manifest.blackboard";
        public const string ManifestReference = "manifest.reference";
        public const string ManifestReferenceDuplicate = "manifest.referenceDuplicate";
        public const string ManifestReferencePath = "manifest.referencePath";

        // tree.json
        public const string TreeMissing = "tree.missing";
        public const string TreeNotObject = "tree.notObject";
        public const string TreeIdMissing = "tree.idMissing";
        public const string TreeIdPattern = "tree.idPattern";
        public const string TreeIdDuplicate = "tree.idDuplicate";
        public const string TreeNodeNotObject = "tree.nodeNotObject";
        public const string TreeChildrenNotArray = "tree.childrenNotArray";
        public const string TreeLimits = "tree.limits";
        public const string TreeEntryNotFound = "tree.entryNotFound";

        // 类型与属性
        public const string TypeUnknown = "type.unknown";
        public const string TypeNotSerializable = "type.notSerializable";
        public const string TypeMissing = "type.missing";
        public const string ShapeChildren = "shape.children";
        public const string ShapeServices = "shape.services";
        public const string ShapeRootChildren = "shape.rootChildren";
        public const string PropertyRequired = "property.required";
        public const string PropertyKind = "property.kind";
        public const string PropertyEnum = "property.enum";
        public const string PropertyUnknown = "property.unknown";
        public const string PropertyValue = "property.value";

        // 引用与嵌套
        public const string ReferenceUnresolved = "reference.unresolved";
        public const string ReferenceCycle = "reference.cycle";
        public const string ReferenceDepth = "reference.depth";
        public const string ReferenceCount = "reference.count";
        public const string ReferenceEscape = "reference.escape";
        public const string SubtreeReference = "subtree.reference";

        // 编译
        public const string CompileFailed = "compile.failed";
        public const string NotifyRejected = "notify.rejected";
    }

    /// <summary>
    /// 一次装载/校验产生的问题集合。**校验器只有一份**（计划 §5.2）：
    /// 加载器、控制面、编辑器都读同一份报告，不各自实现一遍规则。
    /// </summary>
    public sealed class PackageReport
    {
        private readonly List<PackageIssue> m_issues = new List<PackageIssue>();

        public IReadOnlyList<PackageIssue> Issues
        {
            get { return m_issues; }
        }

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        public bool HasErrors
        {
            get { return ErrorCount > 0; }
        }

        public bool IsEmpty
        {
            get { return m_issues.Count == 0; }
        }

        public void Add(PackageIssue issue)
        {
            if (issue == null)
                return;
            m_issues.Add(issue);
            if (issue.Severity == PackageIssueSeverity.Error)
                ErrorCount++;
            else if (issue.Severity == PackageIssueSeverity.Warning)
                WarningCount++;
        }

        public void Error(string code, string where, string message)
        {
            Add(new PackageIssue(PackageIssueSeverity.Error, code, where, message));
        }

        public void Warn(string code, string where, string message)
        {
            Add(new PackageIssue(PackageIssueSeverity.Warning, code, where, message));
        }

        public void Info(string code, string where, string message)
        {
            Add(new PackageIssue(PackageIssueSeverity.Info, code, where, message));
        }

        public void AddRange(PackageReport other)
        {
            if (other == null)
                return;
            for (int i = 0; i < other.m_issues.Count; i++)
                Add(other.m_issues[i]);
        }

        /// <summary>第一条错误（用于"为什么装不上"的一句话回答）。</summary>
        public PackageIssue FirstError
        {
            get
            {
                for (int i = 0; i < m_issues.Count; i++)
                {
                    if (m_issues[i].Severity == PackageIssueSeverity.Error)
                        return m_issues[i];
                }
                return null;
            }
        }

        public bool HasCode(string code)
        {
            for (int i = 0; i < m_issues.Count; i++)
            {
                if (string.Equals(m_issues[i].Code, code, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>逐条文本（控制面回包与日志用）。</summary>
        public List<string> Summarize(int maxLines = 32)
        {
            var lines = new List<string>();
            for (int i = 0; i < m_issues.Count && i < maxLines; i++)
                lines.Add(m_issues[i].Describe());
            if (m_issues.Count > maxLines)
                lines.Add("... (" + (m_issues.Count - maxLines) + " more)");
            return lines;
        }

        public string Summary()
        {
            if (m_issues.Count == 0)
                return "ok";
            if (ErrorCount > 0)
                return ErrorCount + " error(s), " + WarningCount + " warning(s)";
            if (WarningCount > 0)
                return WarningCount + " warning(s)";
            return m_issues.Count + " info";
        }

        public override string ToString()
        {
            return "PackageReport(" + Summary() + ")";
        }
    }
}
