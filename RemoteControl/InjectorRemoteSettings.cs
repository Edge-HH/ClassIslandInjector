using System.Globalization;
using System.Reflection;

namespace ClassIslandInjector.RemoteControl;

/// <summary>远程设置字段类型；数值与 RemoteCI 的 <c>ExtensionParameterType</c> 一一对应。</summary>
internal enum RemoteFieldType
{
    Text = 1,
    Number = 2,
    Switch = 3,
    Select = 4,
    Color = 5
}

/// <summary>字段联动条件（对应 RemoteCI 的 <c>ExtensionFieldCondition</c>）：引用字段的当前值属于 Values 之一即满足，Not 取反。</summary>
internal sealed record RemoteCondition(string Key, IReadOnlyList<string> Values, bool Not = false);

/// <summary>
/// 一个可在 WebUI 远程修改的设置字段描述。本文件不引用任何 RemoteCI 类型，
/// 未安装 RemoteCI 时也能安全加载；由 <see cref="RemoteCiIntegration"/> 转成 RemoteCI 的字段结构。
/// 布局属性（分区 / 卡片 / 联动 / 滑块）照抄本机设置页，见 <see cref="InjectorRemoteLayout"/>。
/// </summary>
internal sealed record RemoteField(
    string Key,
    string Label,
    RemoteFieldType Type,
    string? Description = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<string>? OptionLabels = null,
    string? Placeholder = null)
{
    /// <summary>所在分区（设置页的分节标题）。</summary>
    public string? Section { get; init; }

    /// <summary>所在卡片（设置页的 SettingsExpander 标题）。</summary>
    public string? Card { get; init; }

    public string? CardDescription { get; init; }

    /// <summary>是否为卡片标题行上的总开关。</summary>
    public bool CardToggle { get; init; }

    public double? Step { get; init; }

    public bool Slider { get; init; }

    public IReadOnlyList<RemoteCondition>? VisibleWhen { get; init; }

    public IReadOnlyList<RemoteCondition>? EnabledWhen { get; init; }

    /// <summary>
    /// 教室端本机文件路径：远程写入时拒绝网络共享路径（\\host\share、//host/share）与 URL。
    /// 否则 WebUI 能让教室电脑去访问任意 SMB 主机（顺带发起 Windows 身份认证），或加载外部的样式表 / 视频。
    /// </summary>
    public bool LocalPathOnly { get; init; }

    /// <summary>带卡片名的完整名称（旧版 RemoteCI 不支持卡片时用作平铺标签）。</summary>
    public string QualifiedLabel => Card is null || Card == Label ? Label : $"{Card} · {Label}";
}

/// <summary>远程写入设置的结果（回执文案直接展示在 WebUI 上）。</summary>
internal sealed record RemoteApplyResult(bool Success, string Message);

/// <summary>
/// 样式注入器 ↔ 远程管理（RemoteCI WebUI）的设置映射。
/// <para>
/// 字段与顺序来自 <see cref="InjectorRemoteLayout"/>（逐项照抄本机设置页）；字段 Key 即 <see cref="InjectorSettings"/>
/// 的属性名，一经发布不要改名（WebUI 批量下发按 Key 做部分更新）。页面专用的卡片总开关用 <c>toggle.属性名</c>。
/// 写入规则与设置页的 SaveAndApply 一致：卡片总开关关 = 写 None；打开且类型仍为 None 时自动选第一项；
/// 只改圆角而形状为「跟随 ClassIsland」时切到圆角矩形。
/// </para>
/// </summary>
internal static class InjectorRemoteSettings
{
    /// <summary>「锁定本机样式设置」字段 Key（不属于 InjectorSettings，单独持久化，不受预设/恢复默认影响）。</summary>
    public const string LockKey = "remote.lockLocalEditing";

    /// <summary>
    /// 曾经开放、出于安全考虑已撤回的字段 Key：远程提交里出现时静默忽略（并在回执里注明），
    /// 而不是报「设置项不存在」——否则按旧字段表整页提交的 WebUI 会因为这一项把整批修改都拒掉。
    /// </summary>
    private static readonly HashSet<string> RetiredKeys = new(StringComparer.Ordinal)
    {
        // 排在 FFmpeg 下载源第一位、下载到的 DLL 会被加载进宿主进程，不能交给远程。
        nameof(InjectorSettings.CustomFfmpegDownloadUrl),
    };

    private static readonly Lazy<IReadOnlyList<RemoteBinding>> Map = new(InjectorRemoteLayout.Build);

    /// <summary>全部远程字段（按设置页顺序，锁定开关在最前）。</summary>
    public static IReadOnlyList<RemoteField> Fields => Map.Value.Select(x => x.Field).ToList();

    /// <summary>读取本机当前值（值统一为字符串，开关为 "true"/"false"，数字用不变区域格式）。</summary>
    public static Dictionary<string, string?> ReadValues()
    {
        var settings = InjectorRuntime.Settings;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var binding in Map.Value)
        {
            var field = binding.Field;
            if (field.Key == LockKey)
            {
                values[field.Key] = RemoteManagementState.LockLocalEditing ? "true" : "false";
            }
            else if (binding.EnumToggle is { } toggle)
            {
                values[field.Key] = Equals(toggle.Property.GetValue(settings), toggle.Off) ? "false" : "true";
            }
            else if (binding.Property is { } property)
            {
                var text = Format(property.GetValue(settings));
                // 下拉框里没有的值（如卡片关闭时的 None）上报为空，WebUI 显示「未选择」，原样提交也能通过校验。
                if (field.Type == RemoteFieldType.Select && text != null && field.Options?.Contains(text) != true)
                {
                    text = null;
                }
                else if (field.Type == RemoteFieldType.Color && NormalizeColor(text) is null)
                {
                    text = null;
                }

                values[field.Key] = text;
            }
        }

        return values;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        _ => value.ToString()
    };

    /// <summary>
    /// 写入远程修改的设置（部分更新：只处理 <paramref name="values"/> 中出现的键）。
    /// 必须在 UI 线程调用：设置变更会同步触发注入器重新应用。
    /// </summary>
    public static RemoteApplyResult Apply(IReadOnlyDictionary<string, string?> values, string? requestedBy)
    {
        var bindings = Map.Value.ToDictionary(x => x.Field.Key, StringComparer.Ordinal);
        var errors = new List<string>();
        var parsed = new List<(PropertyInfo Property, object? Value)>();
        var toggles = new List<(RemoteEnumToggle Toggle, bool On)>();
        var ignored = new List<string>();
        bool? lockValue = null;

        foreach (var (key, raw) in values)
        {
            if (!bindings.TryGetValue(key, out var binding))
            {
                if (RetiredKeys.Contains(key))
                {
                    ignored.Add(key);
                }
                else
                {
                    errors.Add($"设置项不存在：{key}");
                }

                continue;
            }

            var field = binding.Field;
            if (key == LockKey || binding.EnumToggle != null)
            {
                if (!TryParseBool(raw, out var on))
                {
                    errors.Add($"“{field.QualifiedLabel}”只能是开启或关闭");
                }
                else if (key == LockKey)
                {
                    lockValue = on;
                }
                else
                {
                    toggles.Add((binding.EnumToggle!, on));
                }

                continue;
            }

            // 下拉框选「未选择」/ 颜色留空：保持原值（整页提交时卡片关闭的下拉框就是空值）。
            if (field.Type is RemoteFieldType.Select or RemoteFieldType.Color && string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var property = binding.Property!;
            if (field.Type == RemoteFieldType.Select && field.Options?.Contains(raw!.Trim()) != true)
            {
                errors.Add($"“{field.QualifiedLabel}”不是有效选项：{raw}");
            }
            else if (field.LocalPathOnly && IsNetworkPath(raw) &&
                     // 原样回传的当前值放行：教室端本机自己选的网络路径会出现在 WebUI 预填里，整页提交不能因此被拒。
                     !string.Equals(raw?.Trim(), (property.GetValue(InjectorRuntime.Settings) as string)?.Trim(),
                         StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"“{field.QualifiedLabel}”只能填写教室端本机路径（不支持网络共享路径或网址）：{raw}");
            }
            else if (field.Type == RemoteFieldType.Color)
            {
                if (NormalizeColor(raw) is { } color)
                {
                    parsed.Add((property, color));
                }
                else
                {
                    errors.Add($"“{field.QualifiedLabel}”不是有效颜色（应为 #RRGGBB 或 #AARRGGBB）：{raw}");
                }
            }
            else if (TryConvert(property.PropertyType, raw, out var converted))
            {
                parsed.Add((property, converted));
            }
            else
            {
                errors.Add($"“{field.QualifiedLabel}”的值无效：{raw}");
            }
        }

        // 任一字段非法则整体不写，避免批量下发时一半生效一半不生效。
        if (errors.Count > 0)
        {
            return new RemoteApplyResult(false, string.Join("；", errors));
        }

        var settings = InjectorRuntime.Settings;
        var properties = Map.Value.Select(x => x.Property ?? x.EnumToggle?.Property).OfType<PropertyInfo>().Distinct().ToList();
        var before = properties.ToDictionary(p => p, p => p.GetValue(settings));
        var provided = parsed.Select(p => p.Property.Name).ToHashSet(StringComparer.Ordinal);
        settings.BeginUpdate();
        try
        {
            foreach (var (property, value) in parsed)
            {
                property.SetValue(settings, value);
            }

            // 卡片总开关在类型之后处理（同设置页 SaveAndApply：开关关 → 写 None，忽略下拉框里残留的选项）。
            foreach (var (toggle, on) in toggles)
            {
                var current = toggle.Property.GetValue(settings);
                if (!on)
                {
                    toggle.Property.SetValue(settings, toggle.Off);
                }
                else if (Equals(current, toggle.Off))
                {
                    // 打开开关但没指定类型：同设置页 AutoSelectOnEnable，自动选第一项。
                    toggle.Property.SetValue(settings, toggle.First);
                }
            }

            // 「持续动画」是真实开关 + 不含「无」的类型下拉：打开时类型仍为 None 则选第一项（同 AutoSelectOnEnable）。
            if ((provided.Contains(nameof(InjectorSettings.AnimationEnabled)) || provided.Contains(nameof(InjectorSettings.AnimationMode))) &&
                settings.AnimationEnabled && settings.AnimationMode == IslandAnimationMode.None)
            {
                settings.AnimationMode = SettingChoices.IslandAnimationModes[0].Value;
            }

            // 与设置页一致：显式改了圆角而形状仍是「跟随 ClassIsland」时切到圆角矩形，否则圆角不会生效。
            var cornerProperty = typeof(InjectorSettings).GetProperty(nameof(InjectorSettings.CornerRadius))!;
            if (!Equals(before[cornerProperty], settings.CornerRadius) &&
                !provided.Contains(nameof(InjectorSettings.Shape)) && settings.Shape == IslandShape.HostDefault)
            {
                settings.Shape = IslandShape.RoundedRectangle;
            }
        }
        finally
        {
            settings.EndUpdate();
        }

        var changed = properties.Count(p => !Equals(before[p], p.GetValue(settings)));

        // 文件关联在设置页里由开关即时注册/移除；远程修改后同样立即生效。
        if (!Equals(before[typeof(InjectorSettings).GetProperty(nameof(InjectorSettings.PresetFileAssociationEnabled))!],
                settings.PresetFileAssociationEnabled))
        {
            InjectorRuntime.EnsureFileAssociation();
        }

        if (lockValue is { } lockLocal && lockLocal != RemoteManagementState.LockLocalEditing)
        {
            RemoteManagementState.SetLock(lockLocal);
            changed++;
        }

        RemoteManagementState.RecordRemoteChange(requestedBy);
        InjectorRuntime.NotifySettingsChangedRemotely();
        var message = changed == 0 ? "设置与当前值一致，无需修改" : $"已更新 {changed} 项样式设置";
        if (ignored.Count > 0)
        {
            message += $"（已忽略不再开放远程修改的设置项：{string.Join("、", ignored)}）";
        }

        return new RemoteApplyResult(true, message);
    }

    /// <summary>网络共享路径（\\host\share、//host/share、\\?\UNC\…）或带协议的网址。</summary>
    internal static bool IsNetworkPath(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        return text.StartsWith(@"\\", StringComparison.Ordinal) ||
               text.StartsWith("//", StringComparison.Ordinal) ||
               text.Contains("://", StringComparison.Ordinal);
    }

    /// <summary>颜色规范为 #AARRGGBB（大写；缺省透明度视为不透明），非法返回 null。</summary>
    internal static string? NormalizeColor(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '#')
        {
            return null;
        }

        var hex = text[1..];
        if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(Uri.IsHexDigit))
        {
            return null;
        }

        if (hex.Length <= 4)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        return "#" + (hex.Length == 6 ? "FF" + hex : hex).ToUpperInvariant();
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        return raw != null && bool.TryParse(raw.Trim(), out value);
    }

    private static bool TryConvert(Type type, string? raw, out object? value)
    {
        value = null;
        if (type == typeof(string))
        {
            value = raw ?? string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (type == typeof(bool))
        {
            var ok = bool.TryParse(text, out var b);
            value = b;
            return ok;
        }

        if (type == typeof(int) || type == typeof(double))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
                double.IsNaN(d) || double.IsInfinity(d))
            {
                return false;
            }

            // 越界值交给属性 setter 里的 Math.Clamp 处理（RemoteCI 侧已按 Min/Max 预检）。
            // 注意别写成 `cond ? (int)x : d` —— 三元表达式会统一成 double，int 属性 SetValue 直接抛异常。
            // 四舍五入要显式 AwayFromZero：Math.Round 默认是银行家舍入（2.5 → 2、3.5 → 4）。
            value = type == typeof(int)
                ? (object)(int)Math.Round(Math.Clamp(d, int.MinValue, int.MaxValue), MidpointRounding.AwayFromZero)
                : d;
            return true;
        }

        if (type.IsEnum)
        {
            if (Enum.TryParse(type, text, ignoreCase: false, out var e) && Enum.IsDefined(type, e!))
            {
                value = e;
                return true;
            }

            return false;
        }

        return false;
    }
}
