using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 节点形状 —— 决定它在包格式里能不能有 <c>children</c> / <c>services</c>。
    /// 形状属于**注册信息的一部分**，包校验器与编辑器都读它，不在别处重复判断。
    /// </summary>
    public enum BtNodeShape
    {
        /// <summary>树根（UE 的 Root）：只允许一个子节点，语义等价 Sequence。</summary>
        Root,

        /// <summary>组合节点：必须有子节点，可挂服务。</summary>
        Composite,

        /// <summary>任务节点：不能有子节点，不能挂服务。</summary>
        Task,

        /// <summary>叶子辅助节点（当前没有内建实现，留给后续扩展）。</summary>
        Leaf
    }

    /// <summary>包格式里的属性值类型（用于校验与编辑器生成控件）。</summary>
    public enum BtPropertyKind
    {
        Bool,
        Int,
        Float,
        String,
        StringList,
        Enum,

        /// <summary>任意 JSON 值（例如 SetBlackboard 的 value），不做类型校验。</summary>
        Any
    }

    /// <summary>
    /// 单个属性的规格：名字、类型、是否必填、默认值、枚举候选。
    /// <see cref="DefaultValue"/> 是**文本形式**，只有编辑器展示与文档用途；
    /// 真正的默认值在节点类里（编译时缺属性就保留类里的默认值，不去解析这个字符串）。
    /// </summary>
    public sealed class BtPropertySpec
    {
        public BtPropertySpec(string name, BtPropertyKind kind, bool required = false,
            string defaultValue = null, string[] allowedValues = null, string description = null)
        {
            Name = name;
            Kind = kind;
            Required = required;
            DefaultValue = defaultValue;
            AllowedValues = allowedValues;
            Description = description;
        }

        public string Name { get; }

        public BtPropertyKind Kind { get; }

        public bool Required { get; }

        public string DefaultValue { get; }

        /// <summary>仅 <see cref="BtPropertyKind.Enum"/> 有意义（大小写不敏感匹配，写回时用这里的规范拼写）。</summary>
        public string[] AllowedValues { get; }

        public string Description { get; }

        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append(Name).Append(':').Append(Kind.ToString().ToLowerInvariant());
            if (Required)
                builder.Append(" required");
            if (!string.IsNullOrEmpty(DefaultValue))
                builder.Append('=').Append(DefaultValue);
            if (AllowedValues != null && AllowedValues.Length > 0)
                builder.Append(" [").Append(string.Join("|", AllowedValues)).Append(']');
            return builder.ToString();
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>节点注册信息：工厂 + 形状 + 属性表 + 能否出现在包格式里。</summary>
    public sealed class BtNodeInfo
    {
        public BtNodeInfo(string typeId, Func<BtNode> factory, BtNodeShape shape,
            bool packageSerializable, BtPropertySpec[] properties)
        {
            TypeId = typeId;
            Factory = factory;
            Shape = shape;
            PackageSerializable = packageSerializable;
            Properties = properties ?? EmptyProperties;
        }

        private static readonly BtPropertySpec[] EmptyProperties = new BtPropertySpec[0];

        /// <summary>规范类型标识（包格式与编辑器写这个拼写）。</summary>
        public string TypeId { get; }

        public Func<BtNode> Factory { get; }

        public BtNodeShape Shape { get; }

        /// <summary>
        /// 能否由包格式构造。false 表示它依赖运行时委托（lambda 类节点），
        /// 只能由代码手工创建：包校验器遇到它要报错，而不是静默失败。
        /// </summary>
        public bool PackageSerializable { get; }

        public IReadOnlyList<BtPropertySpec> Properties { get; }

        public bool AllowsChildren
        {
            get { return Shape == BtNodeShape.Root || Shape == BtNodeShape.Composite; }
        }

        public bool AllowsServices
        {
            get { return Shape == BtNodeShape.Root || Shape == BtNodeShape.Composite; }
        }

        /// <summary>
        /// 按名字找属性规格。**大小写敏感**：属性名进 JSON 就是标识符，
        /// "timeOut" 拼错必须当场报错，而不是"当作没写、静默无效"
        /// （值本身的大小写不敏感只体现在枚举候选上）。
        /// </summary>
        public bool TryGetProperty(string name, out BtPropertySpec spec)
        {
            for (int i = 0; i < Properties.Count; i++)
            {
                if (string.Equals(Properties[i].Name, name, StringComparison.Ordinal))
                {
                    spec = Properties[i];
                    return true;
                }
            }
            spec = null;
            return false;
        }
    }

    /// <summary>装饰器注册信息（装饰器不是树节点，单独一张表）。</summary>
    public sealed class BtDecoratorInfo
    {
        public BtDecoratorInfo(string typeId, Func<BtDecorator> factory,
            bool packageSerializable, BtPropertySpec[] properties)
        {
            TypeId = typeId;
            Factory = factory;
            PackageSerializable = packageSerializable;
            Properties = properties ?? new BtPropertySpec[0];
        }

        public string TypeId { get; }

        public Func<BtDecorator> Factory { get; }

        public bool PackageSerializable { get; }

        public IReadOnlyList<BtPropertySpec> Properties { get; }
    }

    /// <summary>服务注册信息（服务只挂在组合/根节点上）。</summary>
    public sealed class BtServiceInfo
    {
        public BtServiceInfo(string typeId, Func<BtService> factory,
            bool packageSerializable, BtPropertySpec[] properties)
        {
            TypeId = typeId;
            Factory = factory;
            PackageSerializable = packageSerializable;
            Properties = properties ?? new BtPropertySpec[0];
        }

        public string TypeId { get; }

        public Func<BtService> Factory { get; }

        public bool PackageSerializable { get; }

        public IReadOnlyList<BtPropertySpec> Properties { get; }
    }

    /// <summary>属性规格的简写工厂：让注册表那一处读起来像表格。</summary>
    public static class BtProps
    {
        public static BtPropertySpec Bool(string name, bool defaultValue, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.Bool, false,
                defaultValue ? "true" : "false", null, description);
        }

        public static BtPropertySpec Int(string name, int defaultValue, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.Int, false,
                defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture), null, description);
        }

        public static BtPropertySpec Float(string name, float defaultValue, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.Float, false,
                defaultValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), null, description);
        }

        public static BtPropertySpec Str(string name, string defaultValue, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.String, false, defaultValue, null, description);
        }

        /// <summary>必填属性（没有默认值）。</summary>
        public static BtPropertySpec RequiredStr(string name, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.String, true, null, null, description);
        }

        public static BtPropertySpec List(string name, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.StringList, false, null, null, description);
        }

        public static BtPropertySpec Enum(string name, string defaultValue, params string[] allowed)
        {
            return new BtPropertySpec(name, BtPropertyKind.Enum, false, defaultValue, allowed, null);
        }

        public static BtPropertySpec Any(string name, string description = null)
        {
            return new BtPropertySpec(name, BtPropertyKind.Any, false, null, null, description);
        }
    }

    /// <summary>包格式里用到的公共枚举值（校验器与编辑器共用，避免各处手写字符串）。</summary>
    public static class BtSchema
    {
        /// <summary>黑板条目类型（对应 AiBlackboard 里真正使用的 CLR 类型）。</summary>
        public static readonly string[] BlackboardTypes = { "bool", "int", "float", "string", "actor" };

        /// <summary>观察者中断模式（UE 的 Observer Aborts）。</summary>
        public static readonly string[] AbortModes = { "None", "Self", "LowerPriority", "Both" };

        /// <summary>黑板装饰器的查询方式（UE 的 BTDecorator_Blackboard 的四种操作之一）。</summary>
        public static readonly string[] BlackboardQueries = { "IsSet", "IsNotSet", "Compare" };

        /// <summary>黑板比较运算符。</summary>
        public static readonly string[] CompareOperators = { "==", "!=", "<", "<=", ">", ">=" };

        /// <summary>SetBlackboard 可写的值类型。</summary>
        public static readonly string[] ValueKinds = { "bool", "int", "float", "string" };

        /// <summary>动作包播放模式（P1 实现；此处先固化拼写）。</summary>
        public static readonly string[] ActionPackageModes =
            { "Sequence", "Parallel", "RandomOne", "RaceFirstSuccess" };
    }
}
