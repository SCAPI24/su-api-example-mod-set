using Game;
using SuAPICore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Networking/PersonalServerDirectory.cs:PersonalServerDirectory.TryAddOrUpdate
    internal sealed class NetWorldFromLinkProvider : ISuFromLinkProvider
    {
        public string Id => "ScMultiplayer.NetWorld";

        public string DisplayName => "Net World";

        public ExternalContentType IconType => ExternalContentType.World;

        public string Title => "Add Net World From Link";

        public string Instruction =>
            "Enter a DNS name or server address and a display name for this Net World.";

        public string PrimaryLabel => "Link:";

        public string SecondaryLabel => "Name:";

        public int PrimaryMaximumLength => 1024;

        public int SecondaryMaximumLength => 50;

        public bool ShowBusyDialog => false;

        public string GetSuggestedSecondaryValue(string primaryValue,
            string currentSecondaryValue)
        {
            if (!string.IsNullOrWhiteSpace(currentSecondaryValue))
                return currentSecondaryValue;
            return PersonalServerDirectory.TryNormalizeAddress(primaryValue,
                out string normalizedAddress, out _)
                ? normalizedAddress
                : primaryValue?.Trim() ?? string.Empty;
        }

        public bool IsInputValid(string primaryValue, string secondaryValue)
        {
            return !string.IsNullOrWhiteSpace(primaryValue) &&
                !string.IsNullOrWhiteSpace(secondaryValue);
        }

        public Task<SuFromLinkResult> ProcessAsync(string primaryValue,
            string secondaryValue, Progress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PersonalServerDirectory.TryAddOrUpdate(primaryValue, secondaryValue,
                out _, out string error))
            {
                throw new InvalidOperationException(error ??
                    "Unable to save the personal Net World.");
            }
            return Task.FromResult(new SuFromLinkResult());
        }
    }
}
