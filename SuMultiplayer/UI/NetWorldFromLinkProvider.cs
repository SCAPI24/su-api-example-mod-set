using System;
using System.Threading;
using System.Threading.Tasks;
using Engine;
using Game;
using SuAPICore;
using SuMultiplayer.Net;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// “Add Net World From Link”：手动填写服务器地址 + 显示名称，写入个人服务器目录。
    /// 与旧 Mod 的 NetWorldFromLinkProvider 同一入口（SuFromLinkProviders）。
    /// </summary>
    public sealed class NetWorldFromLinkProvider : ISuFromLinkProvider
    {
        public string Id => "SuMultiplayer.NetWorld";
        public string DisplayName => "Net World";
        public ExternalContentType IconType => ExternalContentType.World;
        public string Title => "Add Net World From Link";
        public string Instruction =>
            "Enter a server address (host or host:port) and a display name for this Net World.";
        public string PrimaryLabel => "Link:";
        public string SecondaryLabel => "Name:";
        public int PrimaryMaximumLength => 1024;
        public int SecondaryMaximumLength => 50;
        public bool ShowBusyDialog => false;

        public string GetSuggestedSecondaryValue(string primaryValue,
            string currentSecondaryValue)
        {
            if (!string.IsNullOrWhiteSpace(currentSecondaryValue)) return currentSecondaryValue;
            return PersonalServers.TryNormalizeAddress(primaryValue, out string normalized, out _)
                ? normalized : primaryValue?.Trim() ?? string.Empty;
        }

        public bool IsInputValid(string primaryValue, string secondaryValue) =>
            !string.IsNullOrWhiteSpace(primaryValue) && !string.IsNullOrWhiteSpace(secondaryValue);

        public Task<SuFromLinkResult> ProcessAsync(string primaryValue, string secondaryValue,
            global::Game.Progress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PersonalServers.TryAdd(primaryValue, secondaryValue, out string error))
                throw new InvalidOperationException(error ?? "Unable to save the personal server.");
            return Task.FromResult(new SuFromLinkResult());
        }
    }
}
