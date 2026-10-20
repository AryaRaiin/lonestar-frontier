using Content.Server.Shuttles.Components;
using Content.Shared._CS.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CS.ShuttleCrewStatus;

/// <summary>
/// System that periodically checks player-owned shuttles for active crew and updates IFF label colors accordingly.
/// Only applies to shuttles with the PlayerShuttle flag set (excludes asteroids, wrecks, and other non-player grids).
/// Shuttles with no crew or only disconnected crew show a gray label (can be customized), while shuttles with active crew show normal white labels.
/// </summary>
public sealed partial class ShuttleCrewStatusSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ISharedPlayerManager _playerManager = default!;
    [Dependency] private SharedShuttleSystem _shuttle = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IRobustRandom _random = default!;

    private EntityQuery<TransformComponent> _transformQuery;

    // Base delay and random jitter for scheduled IFF color changes.
    private TimeSpan _baseVisualDelay = TimeSpan.FromSeconds(20);
    private TimeSpan _randomVisualJitter = TimeSpan.FromSeconds(25);
    // Base color used when no active crew is present.
    private Color _inactiveCrewColor = Color.Gray;

    public override void Initialize()
    {
        base.Initialize();

        _transformQuery = GetEntityQuery<TransformComponent>();

        _cfg.OnValueChanged(
            CSCVars.ShuttleCrewUpdateBaseDelaySeconds,
            UpdateBaseVisualDelay,
            invokeImmediately: true);

        _cfg.OnValueChanged(
            CSCVars.ShuttleCrewUpdateRandomJitterSeconds,
            UpdateRandomVisualJitter,
            invokeImmediately: true);

        _cfg.OnValueChanged(
            CSCVars.ShuttleInactiveCrewColor,
            UpdateInactiveCrewColor,
            invokeImmediately: true);

        SubscribeLocalEvent<IFFComponent, MapInitEvent>(OnIFFMapInit);
        SubscribeLocalEvent<EntParentChangedMessage>(OnEntParentChanged);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);

        // Handles disconnect/zombie transitions even when no movement event is fired.
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _cfg.UnsubValueChanged(CSCVars.ShuttleCrewUpdateBaseDelaySeconds, UpdateBaseVisualDelay);
        _cfg.UnsubValueChanged(CSCVars.ShuttleCrewUpdateRandomJitterSeconds, UpdateRandomVisualJitter);
        _cfg.UnsubValueChanged(CSCVars.ShuttleInactiveCrewColor, UpdateInactiveCrewColor);
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    private void UpdateBaseVisualDelay(int seconds)
    {
        _baseVisualDelay = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    private void UpdateRandomVisualJitter(int seconds)
    {
        _randomVisualJitter = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    private void UpdateInactiveCrewColor(string colorText)
    {
        var parsed = Color.TryFromHex(colorText);

        if (parsed == null)
        {
            Log.Warning($"Invalid cvar value for {CSCVars.ShuttleInactiveCrewColor.Name}: '{colorText}'. Falling back to #808080.");
            _inactiveCrewColor = Color.Gray;
            return;
        }

        _inactiveCrewColor = parsed.Value;
    }

    private void OnIFFMapInit(EntityUid uid, IFFComponent component, MapInitEvent args)
    {
        // Only track player shuttles.
        if (!TryComp<ShuttleComponent>(uid, out _))
            return;

        if ((component.Flags & IFFFlags.IsPlayerShuttle) == 0)
            return;

        // Start crew tracking and preserve the original IFF color.
        var crewStatus = EnsureComp<ShuttleCrewStatusComponent>(uid);
        crewStatus.OriginalColor = component.Color;
        crewStatus.HasActiveCrew = HasActivePlayersOnGrid(uid);
        crewStatus.PendingVisualUpdate = false;
    }

    private void OnEntParentChanged(ref EntParentChangedMessage ev)
    {
        var uid = ev.Entity;

        if (!_playerManager.TryGetSessionByEntity(uid, out var session))
            return;

        if (session.Status is SessionStatus.Disconnected or SessionStatus.Zombie)
            return;

        EntityUid? oldGrid = null;
        var newGrid = ev.Transform.GridUid;

        if (ev.OldParent is { } oldParent &&
            _transformQuery.TryGetComponent(oldParent, out var oldParentXform))
        {
            oldGrid = oldParentXform.GridUid;
        }

        if (oldGrid is { } oldGridUid)
            QueueStatusRefresh(oldGridUid);

        if (newGrid is { } newGridUid && newGridUid != oldGrid)
            QueueStatusRefresh(newGridUid);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (!_transformQuery.TryGetComponent(ev.Entity, out var xform))
            return;

        if (xform.GridUid is not { } gridUid)
            return;

        QueueStatusRefresh(gridUid);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        var becameInactive = e.NewStatus is SessionStatus.Disconnected or SessionStatus.Zombie;
        var becameActive = e.NewStatus == SessionStatus.InGame;

        if (!becameInactive && !becameActive)
            return;

        if (e.Session.AttachedEntity is not { } ent)
        {
            if (becameInactive)
                RefreshTrackedPlayerShuttles();

            // Reconnect activation is handled by PlayerAttachedEvent.
            return;
        }

        if (!_transformQuery.TryGetComponent(ent, out var xform))
            return;

        if (xform.GridUid is not { } gridUid)
            return;

        QueueStatusRefresh(gridUid);
    }

    // Refresh all tracked player shuttles so we don't miss the shuttle the player just left in case AttachedEntity is already null.
    private void RefreshTrackedPlayerShuttles()
    {
        var shuttleQuery = EntityQueryEnumerator<ShuttleCrewStatusComponent, IFFComponent>();
        while (shuttleQuery.MoveNext(out var shuttleUid, out _, out var iff))
        {
            if ((iff.Flags & IFFFlags.IsPlayerShuttle) == 0)
                continue;

            QueueStatusRefresh(shuttleUid);
        }
    }

    // If the currently applied state is already correct, cancel any queued opposite transition to prevent flicker.
    private void QueueStatusRefresh(EntityUid gridUid)
    {
        // Required to be a grid.
        if (!TryComp<ShuttleComponent>(gridUid, out _))
            return;

        // Required to have IFF component.
        if (!TryComp<IFFComponent>(gridUid, out var iff))
            return;

        // Required to be a player shuttle.
        if ((iff.Flags & IFFFlags.IsPlayerShuttle) == 0)
            return;

        var crewStatus = EnsureComp<ShuttleCrewStatusComponent>(gridUid);

        // Preserve the original IFF color for restoration when active crew return.
        if (!crewStatus.OriginalColor.HasValue)
            crewStatus.OriginalColor = iff.Color;

        var computedActive = HasActivePlayersOnGrid(gridUid);

        if (computedActive == crewStatus.HasActiveCrew &&
            crewStatus.PendingVisualUpdate &&
            crewStatus.PendingHasActiveCrew != computedActive)
        {
            crewStatus.PendingVisualUpdate = false;
            return;
        }

        var effectiveActive = crewStatus.PendingVisualUpdate
            ? crewStatus.PendingHasActiveCrew
            : crewStatus.HasActiveCrew;

        if (computedActive == effectiveActive)
            return;

        crewStatus.PendingHasActiveCrew = computedActive;
        crewStatus.PendingVisualUpdate = true;
        crewStatus.ApplyVisualAt = _timing.CurTime + GetVisualDelay();
    }

    // Returns the base delay plus a random jitter to vary visual updates.
    private TimeSpan GetVisualDelay()
    {
        if (_randomVisualJitter <= TimeSpan.Zero)
            return _baseVisualDelay;

        var jitterSeconds = _random.NextFloat((float) _randomVisualJitter.TotalSeconds);
        return _baseVisualDelay + TimeSpan.FromSeconds(jitterSeconds);
    }

    // Apply pending IFF color updates once their scheduled time has elapsed.
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ShuttleCrewStatusComponent, IFFComponent>();

        while (query.MoveNext(out var uid, out var crewStatus, out var iff))
        {
            if (!crewStatus.PendingVisualUpdate)
                continue;

            if (now < crewStatus.ApplyVisualAt)
                continue;

            crewStatus.PendingVisualUpdate = false;

            // Skip the color update if the active crew status hasn't changed.
            if (crewStatus.PendingHasActiveCrew == crewStatus.HasActiveCrew)
                continue;

            crewStatus.HasActiveCrew = crewStatus.PendingHasActiveCrew;

            // Restore the original color when active crew are present, mark shuttles without active crew the inactive color.
            if (crewStatus.HasActiveCrew)
            {
                if (crewStatus.OriginalColor.HasValue)
                    _shuttle.SetIFFColor(uid, crewStatus.OriginalColor.Value, iff);
            }
            else
            {
                _shuttle.SetIFFColor(uid, _inactiveCrewColor, iff);
            }
        }
    }

    // Ignore ghost entities when checking for active players.
    private bool HasActivePlayersOnGrid(EntityUid gridUid)
    {
        foreach (var session in _playerManager.Sessions)
        {
            if (session.Status is SessionStatus.Disconnected or SessionStatus.Zombie)
                continue;

            if (session.AttachedEntity is not { } playerEntity)
                continue;

            if (HasComp<GhostComponent>(playerEntity))
                continue;

            if (!_transformQuery.TryGetComponent(playerEntity, out var playerXform))
                continue;

            if (playerXform.GridUid == gridUid)
                return true;
        }

        return false;
    }
}
