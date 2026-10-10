namespace MacroHid.Runtime;

#pragma warning disable CA1416

internal static class AppFirewall
{
    private const string OutboundName = "MacroHID-WeakNet-Out";
    private const string InboundName = "MacroHID-WeakNet-In";

    public static void Block(string executablePath)
    {
        Clear();
        var policy = CreatePolicy();
        Add(policy, OutboundName, executablePath, direction: 2);
        Add(policy, InboundName, executablePath, direction: 1);
    }

    public static void Clear()
    {
        dynamic policy = CreatePolicy();
        TryRemove(policy, OutboundName);
        TryRemove(policy, InboundName);
    }

    private static object CreatePolicy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("无法访问 Windows 防火墙。");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法访问 Windows 防火墙。");
    }

    private static void Add(object policy, string name, string executablePath, int direction)
    {
        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new InvalidOperationException("无法创建防火墙规则。");
        dynamic rule = Activator.CreateInstance(ruleType)
            ?? throw new InvalidOperationException("无法创建防火墙规则。");
        rule.Name = name;
        rule.ApplicationName = executablePath;
        rule.Direction = direction;
        rule.Action = 0;
        rule.Enabled = true;
        rule.Profiles = 0x7FFFFFFF;
        rule.Protocol = 256;
        rule.InterfaceTypes = "All";
        ((dynamic)policy).Rules.Add(rule);
    }

    private static void TryRemove(dynamic policy, string name)
    {
        try
        {
            policy.Rules.Remove(name);
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            // 规则不存在。
        }
    }
}
