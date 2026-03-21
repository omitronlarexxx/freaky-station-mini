//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Common.CCVar;
using Content.Goobstation.Common.ServerCurrency;
using Content.Corvax.Interfaces.Shared;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Player;
using System.Linq;

namespace Content.Goobstation.Server.ServerCurrency;

/// <summary>
/// Grants coins for time spent alive.
/// </summary>
public sealed class ServerCurrencyAccrualSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly ICommonCurrencyManager _currency = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    private ISharedSponsorsManager? _sponsors;

    private readonly Dictionary<NetUserId, float> _aliveSeconds = new();

    private int _coinsPerPlayer = 10;
    private int _serverMultiplier = 1;
    private double _sponsorMultiplier = 2.0;
    private int _minPlayers = 1;
    private bool _useLowpopMultiplier = true;
    private double _lowpopMultiplierStrength = 1.0;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, GoobCVars.GoobcoinsPerPlayer, v => _coinsPerPlayer = Math.Max(0, v), true);
        Subs.CVar(_cfg, GoobCVars.GoobcoinServerMultiplier, v => _serverMultiplier = Math.Max(0, v), true);
        Subs.CVar(_cfg, GoobCVars.GoobcoinSponsorMultiplier, v => _sponsorMultiplier = Math.Max(1, v), true);
        Subs.CVar(_cfg, GoobCVars.GoobcoinMinPlayers, v => _minPlayers = Math.Max(1, v), true);
        Subs.CVar(_cfg, GoobCVars.GoobcoinUseLowpopMultiplier, v => _useLowpopMultiplier = v, true);
        Subs.CVar(_cfg, GoobCVars.GoobcoinLowpopMultiplierStrength, v => _lowpopMultiplierStrength = Math.Max(0, v), true);
        IoCManager.Instance!.TryResolveType(out _sponsors);

        _player.PlayerStatusChanged += OnPlayerStatusChanged;
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _player.PlayerStatusChanged -= OnPlayerStatusChanged;
        FlushAllPending();
        _aliveSeconds.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (frameTime <= 0f)
            return;

        var inGamePlayers = _player.Sessions.Count(p => p.Status == SessionStatus.InGame);
        if (inGamePlayers <= 0)
            return;

        var coinsPerMinute = CalculateCoinsPerMinute(inGamePlayers);
        if (coinsPerMinute <= 0)
            return;

        foreach (var session in _player.Sessions)
        {
            if (session.Status != SessionStatus.InGame || !IsAlive(session))
                continue;

            var accumulated = _aliveSeconds.GetValueOrDefault(session.UserId, 0f) + frameTime;
            var wholeMinutes = (int) MathF.Floor(accumulated / 60f);

            if (wholeMinutes > 0)
            {
                var reward = wholeMinutes * coinsPerMinute;
                reward = ApplySponsorBonus(session.UserId, reward);
                if (reward > 0)
                    _currency.AddCurrency(session.UserId, reward);
            }

            _aliveSeconds[session.UserId] = accumulated - wholeMinutes * 60f;
        }
    }

    private int CalculateCoinsPerMinute(int inGamePlayers)
    {
        if (inGamePlayers < _minPlayers && !_useLowpopMultiplier)
            return 0;

        var coins = _coinsPerPlayer * _serverMultiplier;
        if (coins <= 0)
            return 0;

        if (_useLowpopMultiplier && inGamePlayers < _minPlayers)
        {
            var diff = _minPlayers - inGamePlayers;
            coins = (int) MathF.Round(coins * (1f + diff * (float) _lowpopMultiplierStrength));
        }

        return Math.Max(0, coins);
    }

    private bool IsAlive(ICommonSession session)
    {
        if (session.AttachedEntity is not { } ent || !TryComp<MobStateComponent>(ent, out var state))
            return false;

        return state.CurrentState is MobState.Alive or MobState.Critical;
    }

    private void FlushAllPending()
    {
        var players = _player.Sessions.Count(p => p.Status == SessionStatus.InGame);
        var coinsPerMinute = CalculateCoinsPerMinute(players);
        if (coinsPerMinute <= 0)
            return;

        foreach (var (userId, seconds) in _aliveSeconds.ToArray())
        {
            var reward = (int) MathF.Round(seconds / 60f * coinsPerMinute);
            reward = ApplySponsorBonus(userId, reward);
            if (reward > 0)
                _currency.AddCurrency(userId, reward);
        }
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus != SessionStatus.Disconnected)
            return;

        if (!_aliveSeconds.TryGetValue(e.Session.UserId, out var seconds))
            return;

        _aliveSeconds.Remove(e.Session.UserId);

        var players = _player.Sessions.Count(p => p.Status == SessionStatus.InGame);
        var coinsPerMinute = CalculateCoinsPerMinute(players);
        if (coinsPerMinute <= 0)
            return;

        var reward = (int) MathF.Round(seconds / 60f * coinsPerMinute);
        reward = ApplySponsorBonus(e.Session.UserId, reward);
        if (reward > 0)
            _currency.AddCurrency(e.Session.UserId, reward);
    }

    private int ApplySponsorBonus(NetUserId userId, int reward)
    {
        if (reward <= 0)
            return 0;

        if (_sponsors == null || !_sponsors.TryGetServerPrototypes(userId, out _))
            return reward;

        return Math.Max(1, (int) MathF.Round((float) (reward * _sponsorMultiplier)));
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        FlushAllPending();
        _aliveSeconds.Clear();
    }
}
