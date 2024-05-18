// All player logic was put into this class. We could also split it into several
// smaller components, but this would result in many GetComponent calls and a
// more complex syntax.
//
// The default Player class takes care of the basic player logic like the state
// machine and some properties like damage and defense.
//
// The Player class stores the maximum experience for each level in a simple
// array. So the maximum experience for level 1 can be found in expMax[0] and
// the maximum experience for level 2 can be found in expMax[1] and so on. The
// player's health and mana are also level dependent in most MMORPGs, hence why
// there are hpMax and mpMax arrays too. We can find out a players's max health
// in level 1 by using hpMax[0] and so on.
//
// The class also takes care of selection handling, which detects 3D world
// clicks and then targets/navigates somewhere/interacts with someone.
//
// Animations are not handled by the NetworkAnimator because it's still very
// buggy and because it can't really react to movement stops fast enough, which
// results in moonwalking. Not synchronizing animations over the network will
// also save us bandwidth
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Mirror;
using TMPro;

[Serializable] public class UnityEventPlayer : UnityEvent<Player> {}

[RequireComponent(typeof(Experience))]
[RequireComponent(typeof(Intelligence))]
[RequireComponent(typeof(Strength))]
[RequireComponent(typeof(PlayerChat))]
[RequireComponent(typeof(PlayerCrafting))]
[RequireComponent(typeof(PlayerGameMasterTool))]
[RequireComponent(typeof(PlayerGuild))]
[RequireComponent(typeof(PlayerIndicator))]
[RequireComponent(typeof(PlayerInventory))]
[RequireComponent(typeof(PlayerItemMall))]
[RequireComponent(typeof(PlayerLooting))]
[RequireComponent(typeof(PlayerMountControl))]
[RequireComponent(typeof(PlayerNpcRevive))]
[RequireComponent(typeof(PlayerNpcTeleport))]
[RequireComponent(typeof(PlayerNpcTrading))]
[RequireComponent(typeof(PlayerParty))]
[RequireComponent(typeof(PlayerPetControl))]
[RequireComponent(typeof(PlayerQuests))]
[RequireComponent(typeof(PlayerSkillbar))]
[RequireComponent(typeof(PlayerSkills))]
[RequireComponent(typeof(PlayerTrading))]
[RequireComponent(typeof(NetworkName))]
public partial class Player : Entity
{
    // fields for all player components to avoid costly GetComponent calls
    [Header("Components")]
    public Experience experience;
    public Intelligence intelligence;
    public Strength strength;
    public PlayerChat chat;
    public PlayerCrafting crafting;
    public PlayerGameMasterTool gameMasterTool;
    public PlayerGuild guild;
    public PlayerIndicator indicator;
    public PlayerInventory inventory;
    public PlayerItemMall itemMall;
    public PlayerLooting looting;
    public PlayerMountControl mountControl;
    public PlayerNpcRevive npcRevive;
    public PlayerNpcTeleport npcTeleport;
    public PlayerNpcTrading npcTrading;
    public PlayerParty party;
    public PlayerPetControl petControl;
    public PlayerQuests quests;
    public PlayerSkillbar skillbar;
    public PlayerTrading trading;

    [Header("Text Meshes")]
    public TextMeshPro nameOverlay;
    public Color nameOverlayDefaultColor = Color.white;
    public Color nameOverlayOffenderColor = Color.magenta;
    public Color nameOverlayMurdererColor = Color.red;
    public Color nameOverlayPartyColor = new Color(0.341f, 0.965f, 0.702f);
    public string nameOverlayGameMasterPrefix = "[GM] ";

    [Header("Icons")]
    public Sprite classIcon; // for character selection
    public Sprite portraitIcon; // for top left portrait

    // some meta info
    [HideInInspector] public string account = "";
    [HideInInspector] public string className = "";

    // keep the GM flag in here and the controls in PlayerGameMaster.cs:
    // -> we need the flag for NameOverlay prefix anyway
    // -> it might be needed outside of PlayerGameMaster for other GM specific
    //    mechanics/checks later
    // -> this way we can use SyncToObservers for the flag, and SyncToOwner for
    //    everything else in PlayerGameMaster component. this is a LOT easier.
    [SyncVar] public bool isGameMaster;

    // localPlayer singleton for easier access from UI scripts etc.
    public static Player localPlayer;

    // speed
    public override float speed =>
        // mount speed if mounted, regular speed otherwise
        mountControl.activeMount != null && mountControl.activeMount.health.current > 0
            ? mountControl.activeMount.speed
            : base.speed;

    // item cooldowns
    // it's based on a 'cooldownCategory' that can be set in ScriptableItems.
    // -> they can use their own name for a cooldown that only applies to them
    // -> they can use a category like 'HealthPotion' for a shared cooldown
    //    amongst all health potions
    // => we could use hash(category) as key to significantly reduce bandwidth,
    //    but we don't anymore because it makes database saving easier.
    //    otherwise we would have to find the category from a hash.
    // => IMPORTANT: cooldowns need to be saved in database so that long
    //    cooldowns can't be circumvented by logging out and back in again.
    internal readonly SyncDictionary<string, double> itemCooldowns =
        new SyncDictionary<string, double>();

    [Header("Interaction")]
    public float interactionRange = 4;
    public bool localPlayerClickThrough = true; // click selection goes through localplayer. feels best.
    public KeyCode cancelActionKey = KeyCode.Escape;

    [Tooltip("Being stunned interrupts the cast. Enable this option to continue the cast afterwards.")]
    public bool continueCastAfterStunned = true;

    [Header("PvP")]
    public BuffSkill offenderBuff;
    public BuffSkill murdererBuff;

    // when moving into attack range of a target, we always want to move a
    // little bit closer than necessary to tolerate for latency and other
    // situations where the target might have moved away a little bit already.
    [Header("Movement")]
    [Range(0.1f, 1)] public float attackToMoveRangeRatio = 0.8f;

    // some commands should have delays to avoid DDOS, too much database usage
    // or brute forcing coupons etc. we use one riskyAction timer for all.
    [SyncVar, HideInInspector] public double nextRiskyActionTime = 0; // double for long term precision

    // the next target to be set if we try to set it while casting
    [SyncVar, HideInInspector] public Entity nextTarget;

    // cache players to save lots of computations
    // (otherwise we'd have to iterate NetworkServer.objects all the time)
    // => on server: all online players
    // => on client: all observed players
    public static Dictionary<string, Player> onlinePlayers = new Dictionary<string, Player>();

    // first allowed logout time after combat
    public double allowedLogoutTime => lastCombatTime + ((NetworkManagerMMO)NetworkManager.singleton).combatLogoutDelay;
    public double remainingLogoutTime => NetworkTime.time < allowedLogoutTime ? (allowedLogoutTime - NetworkTime.time) : 0;

    // helper variable to remember which skill to use when we walked close enough
    [HideInInspector] public int useSkillWhenCloser = -1;

    // networkbehaviour ////////////////////////////////////////////////////////
    public override void OnStartLocalPlayer()
    {
        // set singleton
        localPlayer = this;

        // setup camera targets
        GameObject.FindWithTag("MinimapCamera").GetComponent<CopyPosition>().target = transform;
    }

    protected override void Start()
    {
        // do nothing if not spawned (=for character selection previews)
        if (!isServer && !isClient) return;

        base.Start();
        onlinePlayers[name] = this;
    }

    void LateUpdate()
    {
        // pass parameters to animation state machine
        // => passing the states directly is the most reliable way to avoid all
        //    kinds of glitches like movement sliding, attack twitching, etc.
        // => make sure to import all looping animations like idle/run/attack
        //    with 'loop time' enabled, otherwise the client might only play it
        //    once
        // => MOVING state is set to local IsMovement result directly. otherwise
        //    we would see animation latencies for rubberband movement if we
        //    have to wait for MOVING state to be received from the server
        // => MOVING checks if !CASTING because there is a case in UpdateMOVING
        //    -> SkillRequest where we still slide to the final position (which
        //    is good), but we should show the casting animation then.
        // => skill names are assumed to be boolean parameters in animator
        //    so we don't need to worry about an animation number etc.
        if (isClient) // no need for animations on the server
        {
            // now pass parameters after any possible rebinds
            foreach (Animator anim in GetComponentsInChildren<Animator>())
            {
                anim.SetBool("MOVING", movement.IsMoving() && !mountControl.IsMounted());
                anim.SetBool("CASTING", state == "CASTING");
                anim.SetBool("STUNNED", state == "STUNNED");
                anim.SetBool("MOUNTED", mountControl.IsMounted()); // for seated animation
                anim.SetBool("DEAD", state == "DEAD");
                foreach (Skill skill in skills.skills)
                    if (skill.level > 0 && !(skill.data is PassiveSkill))
                        anim.SetBool(skill.name, skill.CastTimeRemaining() > 0);
            }
        }
    }

    void OnDestroy()
    {
        // try to remove from onlinePlayers first, NO MATTER WHAT
        // -> we can not risk ever not removing it. do this before any early
        //    returns etc.
        // -> ONLY remove if THIS object was saved. this avoids a bug where
        //    a host selects a character preview, then joins the game, then
        //    only after the end of the frame the preview is destroyed,
        //    OnDestroy is called and the preview would actually remove the
        //    world player from onlinePlayers. hence making guild management etc
        //    impossible.
        if (onlinePlayers.TryGetValue(name, out Player entry) && entry == this)
            onlinePlayers.Remove(name);

        // do nothing if not spawned (=for character selection previews)
        if (!isServer && !isClient) return;

        if (isLocalPlayer)
            localPlayer = null;
    }

    // finite state machine events - status based //////////////////////////////
    // status based events
    bool EventDied() =>
        health.current == 0;

    bool EventTargetDisappeared() =>
        target == null;

    bool EventTargetDied() =>
        target != null && target.health.current == 0;

    bool EventSkillRequest() =>
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count;

    bool EventSkillFinished() =>
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count &&
        skills.skills[skills.currentSkill].CastTimeRemaining() == 0;

    bool EventMoveStart() =>
        state != "MOVING" && movement.IsMoving(); // only fire when started moving

    bool EventMoveEnd() =>
        state == "MOVING" && !movement.IsMoving(); // only fire when stopped moving

    bool EventTradeStarted()
    {
        // did someone request a trade? and did we request a trade with him too?
        Player player = trading.FindPlayerFromInvitation();
        return player != null && player.trading.requestFrom == name;
    }

    bool EventTradeDone() =>
        // trade canceled or finished?
        state == "TRADING" && trading.requestFrom == "";

    bool EventCraftingStarted()
    {
        bool result = crafting.requestPending;
        crafting.requestPending = false;
        return result;
    }

    bool EventCraftingDone() =>
        state == "CRAFTING" && NetworkTime.time > crafting.endTime;

    bool EventStunned() =>
        NetworkTime.time <= stunTimeEnd;

    // finite state machine events - command based /////////////////////////////
    // client calls command, command sets a flag, event reads and resets it
    // => we use a set so that we don't get ultra long queues etc.
    // => we use set.Return to read and clear values
    HashSet<string> cmdEvents = new HashSet<string>();

    [Command]
    public void CmdRespawn() { cmdEvents.Add("Respawn"); }
    bool EventRespawn() { return cmdEvents.Remove("Respawn"); }

    [Command]
    public void CmdCancelAction() { cmdEvents.Add("CancelAction"); }
    bool EventCancelAction() { return cmdEvents.Remove("CancelAction"); }

    // finite state machine - server ///////////////////////////////////////////
    [Server]
    string UpdateServer_IDLE()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            return "DEAD";
        }
        if (EventStunned())
        {
            movement.Reset();
            return "STUNNED";
        }
        if (EventCancelAction())
        {
            // the only thing that we can cancel is the target
            target = null;
            return "IDLE";
        }
        if (EventTradeStarted())
        {
            // cancel casting (if any), set target, go to trading
            skills.CancelCast(); // just in case
            target = trading.FindPlayerFromInvitation();
            return "TRADING";
        }
        if (EventCraftingStarted())
        {
            // cancel casting (if any), go to crafting
            skills.CancelCast(); // just in case
            return "CRAFTING";
        }
        if (EventMoveStart())
        {
            // cancel casting (if any)
            skills.CancelCast();
            return "MOVING";
        }
        if (EventSkillRequest())
        {
            // don't cast while mounted
            // (no MOUNTED state because we'd need MOUNTED_STUNNED, etc. too)
            if (!mountControl.IsMounted())
            {
                // user wants to cast a skill.
                // check self (alive, mana, weapon etc.) and target and distance
                Skill skill = skills.skills[skills.currentSkill];
                nextTarget = target; // return to this one after any corrections by CastCheckTarget
                if (skills.CastCheckSelf(skill) &&
                    skills.CastCheckTarget(skill) &&
                    skills.CastCheckDistance(skill, out Vector3 destination))
                {
                    // start casting and cancel movement in any case
                    // (player might move into attack range * 0.8 but as soon as we
                    //  are close enough to cast, we fully commit to the cast.)
                    movement.Reset();
                    skills.StartCast(skill);
                    return "CASTING";
                }
                else
                {
                    // checks failed. reset the attempted current skill.
                    skills.currentSkill = -1;
                    nextTarget = null; // nevermind, clear again (otherwise it's shown in UITarget)
                    return "IDLE";
                }
            }
        }
        if (EventSkillFinished()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventCraftingDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "IDLE"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_MOVING()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            return "DEAD";
        }
        if (EventStunned())
        {
            movement.Reset();
            return "STUNNED";
        }
        if (EventMoveEnd())
        {
            // finished moving. do whatever we did before.
            return "IDLE";
        }
        if (EventCancelAction())
        {
            // cancel casting (if any) and stop moving
            skills.CancelCast();
            //movement.Reset(); <- done locally. doing it here would reset localplayer to the slightly behind server position otherwise
            return "IDLE";
        }
        if (EventTradeStarted())
        {
            // cancel casting (if any), stop moving, set target, go to trading
            skills.CancelCast();
            movement.Reset();
            target = trading.FindPlayerFromInvitation();
            return "TRADING";
        }
        if (EventCraftingStarted())
        {
            // cancel casting (if any), stop moving, go to crafting
            skills.CancelCast();
            movement.Reset();
            return "CRAFTING";
        }
        // SPECIAL CASE: Skill Request while doing rubberband movement
        // -> we don't really need to react to it
        // -> we could just wait for move to end, then react to request in IDLE
        // -> BUT player position on server always lags behind in rubberband movement
        // -> SO there would be a noticeable delay before we start to cast
        //
        // SOLUTION:
        // -> start casting as soon as we are in range
        // -> BUT don't ResetMovement. instead let it slide to the final position
        //    while already starting to cast
        // -> NavMeshAgentRubberbanding won't accept new positions while casting
        //    anyway, so this is fine
        if (EventSkillRequest())
        {
            // don't cast while mounted
            // (no MOUNTED state because we'd need MOUNTED_STUNNED, etc. too)
            if (!mountControl.IsMounted())
            {
                Skill skill = skills.skills[skills.currentSkill];
                if (skills.CastCheckSelf(skill) &&
                    skills.CastCheckTarget(skill) &&
                    skills.CastCheckDistance(skill, out Vector3 destination))
                {
                    //Debug.Log("MOVING->EventSkillRequest: early cast started while sliding to destination...");
                    // movement.Reset(); <- DO NOT DO THIS.
                    skills.StartCast(skill);
                    return "CASTING";
                }
            }
        }
        if (EventMoveStart()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventCraftingDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "MOVING"; // nothing interesting happened
    }

    void UseNextTargetIfAny()
    {
        // use next target if the user tried to target another while casting
        // (target is locked while casting so skill isn't applied to an invalid
        //  target accidentally)
        if (nextTarget != null)
        {
            target = nextTarget;
            nextTarget = null;
        }
    }

    [Server]
    string UpdateServer_CASTING()
    {
        // keep looking at the target for server & clients (only Y rotation)
        if (target && movement.DoCombatLookAt())
            movement.LookAtY(target.transform.position);

        // events sorted by priority (e.g. target doesn't matter if we died)
        //
        // IMPORTANT: nextTarget might have been set while casting, so make sure
        // to handle it in any case here. it should definitely be null again
        // after casting was finished.
        // => this way we can reliably display nextTarget on the client if it's
        //    != null, so that UITarget always shows nextTarget>target
        //    (this just feels better)
        if (EventDied())
        {
            // we died.
            UseNextTargetIfAny(); // if user selected a new target while casting
            return "DEAD";
        }
        if (EventStunned())
        {
            // cancel cast & movement
            // (only clear current skill if we don't continue cast after stunned)
            skills.CancelCast(!continueCastAfterStunned);
            movement.Reset();
            return "STUNNED";
        }
        if (EventMoveStart())
        {
            // we do NOT cancel the cast if the player moved, and here is why:
            // * local player might move into cast range and then try to cast.
            // * server then receives the Cmd, goes to CASTING state, then
            //   receives one of the last movement updates from the local player
            //   which would cause EventMoveStart and cancel the cast.
            // * this is the price for rubberband movement.
            // => if the player wants to cast and got close enough, then we have
            //    to fully commit to it. there is no more way out except via
            //    cancel action. any movement in here is to be rejected.
            //    (many popular MMOs have the same behaviour too)
            //

            // we do NOT reset movement either. allow sliding to final position.
            // (NavMeshAgentRubberbanding doesn't accept new ones while CASTING)
            //movement.Reset(); <- DO NOT DO THIS

            // we do NOT return "CASTING". EventMoveStart would constantly fire
            // while moving for skills that allow movement. hence we would
            // always return "CASTING" here and never get to the castfinished
            // code below.
            //return "CASTING";
        }
        if (EventCancelAction())
        {
            // cancel casting
            skills.CancelCast();
            UseNextTargetIfAny(); // if user selected a new target while casting
            return "IDLE";
        }
        if (EventTradeStarted())
        {
            // cancel casting (if any), stop moving, set target, go to trading
            skills.CancelCast();
            movement.Reset();

            // set target to trade target instead of next target (clear that)
            target = trading.FindPlayerFromInvitation();
            nextTarget = null;
            return "TRADING";
        }
        if (EventTargetDisappeared())
        {
            // cancel if the target matters for this skill
            if (skills.skills[skills.currentSkill].cancelCastIfTargetDied)
            {
                skills.CancelCast();
                UseNextTargetIfAny(); // if user selected a new target while casting
                return "IDLE";
            }
        }
        if (EventTargetDied())
        {
            // cancel if the target matters for this skill
            if (skills.skills[skills.currentSkill].cancelCastIfTargetDied)
            {
                skills.CancelCast();
                UseNextTargetIfAny(); // if user selected a new target while casting
                return "IDLE";
            }
        }
        if (EventSkillFinished())
        {
            // apply the skill after casting is finished
            // note: we don't check the distance again. it's more fun if players
            //       still cast the skill if the target ran a few steps away
            Skill skill = skills.skills[skills.currentSkill];

            // apply the skill on the target
            skills.FinishCast(skill);

            // clear current skill for now
            skills.currentSkill = -1;

            // use next target if the user tried to target another while casting
            UseNextTargetIfAny();

            // go back to IDLE
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventCraftingStarted()) {} // don't care
        if (EventCraftingDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "CASTING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_STUNNED()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            return "DEAD";
        }
        if (EventStunned())
        {
            return "STUNNED";
        }

        // go back to idle if we aren't stunned anymore and process all new
        // events there too
        return "IDLE";
    }

    [Server]
    string UpdateServer_TRADING()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died, stop trading. other guy will receive targetdied event.
            trading.Cleanup();
            return "DEAD";
        }
        if (EventStunned())
        {
            // stop trading
            skills.CancelCast();
            movement.Reset();
            trading.Cleanup();
            return "STUNNED";
        }
        if (EventMoveStart())
        {
            // reject movement while trading
            movement.Reset();
            return "TRADING";
        }
        if (EventCancelAction())
        {
            // stop trading
            trading.Cleanup();
            return "IDLE";
        }
        if (EventTargetDisappeared())
        {
            // target disconnected, stop trading
            trading.Cleanup();
            return "IDLE";
        }
        if (EventTargetDied())
        {
            // target died, stop trading
            trading.Cleanup();
            return "IDLE";
        }
        if (EventTradeDone())
        {
            // someone canceled or we finished the trade. stop trading
            trading.Cleanup();
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventCraftingStarted()) {} // don't care
        if (EventCraftingDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTradeStarted()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "TRADING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_CRAFTING()
    {

        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died, stop crafting
            return "DEAD";
        }
        if (EventStunned())
        {
            // stop crafting
            movement.Reset();
            return "STUNNED";
        }
        if (EventMoveStart())
        {
            // reject movement while crafting
            movement.Reset();
            return "CRAFTING";
        }
        if (EventCraftingDone())
        {
            // finish crafting
            crafting.Craft();
            return "IDLE";
        }
        if (EventCancelAction()) {} // don't care. user pressed craft, we craft.
        if (EventTargetDisappeared()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTradeStarted()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventCraftingStarted()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "CRAFTING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_DEAD()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventRespawn())
        {
            // revive to closest spawn, with 50% health, then go to idle
            Transform start = NetworkManagerMMO.GetNearestStartPosition(transform.position);
            // warp to new position (never use transform.position for agents!)
            //
            // NOTE: Warp sends RpcWarp to clients automatically, but the
            //       player has 0 observers since it was hidden until now.
            //       SpawnMessage -> NetworkNavMeshAgent.OnDeserialize has an
            //       'if initialState then Warp' check which moves it on clients.
            movement.Warp(start.position);
            Revive(0.5f);
            return "IDLE";
        }
        if (EventMoveStart())
        {
            // this should never happen, rubberband should prevent from moving
            // while dead.
            Debug.LogWarning("Player " + name + " moved while dead. This should not happen.");
            return "DEAD";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventDied()) {} // don't care
        if (EventCancelAction()) {} // don't care
        if (EventTradeStarted()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventCraftingStarted()) {} // don't care
        if (EventCraftingDone()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "DEAD"; // nothing interesting happened
    }

    [Server]
    protected override string UpdateServer()
    {
        if (state == "IDLE")     return UpdateServer_IDLE();
        if (state == "MOVING")   return UpdateServer_MOVING();
        if (state == "CASTING")  return UpdateServer_CASTING();
        if (state == "STUNNED")  return UpdateServer_STUNNED();
        if (state == "TRADING")  return UpdateServer_TRADING();
        if (state == "CRAFTING") return UpdateServer_CRAFTING();
        if (state == "DEAD")     return UpdateServer_DEAD();
        Debug.LogError("invalid state:" + state);
        return "IDLE";
    }

    // finite state machine - client ///////////////////////////////////////////
    [Client]
    protected override void UpdateClient()
    {
        if (state == "IDLE" || state == "MOVING")
        {
            if (isLocalPlayer)
            {
                // cancel action if escape key was pressed
                if (Input.GetKeyDown(cancelActionKey))
                {
                    // reset locally because we use rubberband movement
                    movement.Reset();
                    CmdCancelAction();
                }

                // trying to cast a skill on a monster that wasn't in range?
                // then check if we walked into attack range by now
                if (useSkillWhenCloser != -1)
                {
                    // can we still attack the target? maybe it was switched.
                    if (CanAttack(target))
                    {
                        // in range already?
                        // -> we don't use CastCheckDistance because we want to
                        // move a bit closer (attackToMoveRangeRatio)
                        float range = skills.skills[useSkillWhenCloser].castRange * attackToMoveRangeRatio;
                        if (Utils.ClosestDistance(this, target) <= range)
                        {
                            // then stop moving and start attacking
                            ((PlayerSkills)skills).CmdUse(useSkillWhenCloser);

                            // reset
                            useSkillWhenCloser = -1;
                        }
                        // otherwise keep walking there. the target might move
                        // around or run away, so we need to keep adjusting the
                        // destination all the time
                        else
                        {
                            //Debug.Log("walking closer to target...");
                            Vector3 destination = Utils.ClosestPoint(target, transform.position);
                            movement.Navigate(destination, range);
                        }
                    }
                    // otherwise reset
                    else useSkillWhenCloser = -1;
                }
            }
        }
        else if (state == "CASTING")
        {
            // keep looking at the target for server & clients (only Y rotation)
            if (target && movement.DoCombatLookAt())
                movement.LookAtY(target.transform.position);

            if (isLocalPlayer)
            {
                // simply reset any client sided movement
                movement.Reset();

                // cancel action if escape key was pressed
                if (Input.GetKeyDown(cancelActionKey)) CmdCancelAction();
            }
        }
        else if (state == "STUNNED")
        {
            if (isLocalPlayer)
            {
                // simply reset any client sided movement
                movement.Reset();

                // cancel action if escape key was pressed
                if (Input.GetKeyDown(cancelActionKey)) CmdCancelAction();
            }
        }
        else if (state == "TRADING") {}
        else if (state == "CRAFTING") {}
        else if (state == "DEAD") {}
        else Debug.LogError("invalid state:" + state);
    }

    // overlays ////////////////////////////////////////////////////////////////
    protected override void UpdateOverlays()
    {
        base.UpdateOverlays();

        if (nameOverlay != null)
        {
            // only players need to copy names to name overlay. it never changes
            // for monsters / npcs.
            nameOverlay.text = name;

            // find local player (null while in character selection)
            if (localPlayer != null)
            {
                // note: murderer has higher priority (a player can be a murderer and an
                // offender at the same time)
                if (IsMurderer())
                    nameOverlay.color = nameOverlayMurdererColor;
                else if (IsOffender())
                    nameOverlay.color = nameOverlayOffenderColor;
                // member of the same party
                else if (localPlayer.party.InParty() && localPlayer.party.party.Contains(name))
                    nameOverlay.color = nameOverlayPartyColor;
                // otherwise default
                else
                    nameOverlay.color = nameOverlayDefaultColor;
            }
        }
    }

    // skill finished event & pending actions //////////////////////////////////
    // pending actions while casting. to be applied after cast.
    [HideInInspector] public int pendingSkill = -1;
    [HideInInspector] public Vector3 pendingDestination;
    [HideInInspector] public bool pendingDestinationValid;

    // client event when skill cast finished on server
    // -> useful for follow up attacks etc.
    //    (doing those on server won't really work because the target might have
    //     moved, in which case we need to follow, which we need to do on the
    //     client)
    [Client]
    public void OnSkillCastFinished(Skill skill)
    {
        if (!isLocalPlayer) return;

        // tried to click move somewhere?
        if (pendingDestinationValid)
        {
            movement.Navigate(pendingDestination, 0);
        }
        // user pressed another skill button?
        else if (pendingSkill != -1)
        {
            ((PlayerSkills)skills).TryUse(pendingSkill, true);
        }
        // otherwise do follow up attack if no interruptions happened
        else if (skill.followupDefaultAttack)
        {
            ((PlayerSkills)skills).TryUse(0, true);
        }

        // clear pending actions in any case
        pendingSkill = -1;
        pendingDestinationValid = false;
    }

    // combat //////////////////////////////////////////////////////////////////
    [Server]
    public void OnDamageDealtTo(Entity victim)
    {
        // attacked an innocent player
        if (victim is Player && ((Player)victim).IsInnocent())
        {
            // start offender if not a murderer yet
            if (!IsMurderer()) StartOffender();
        }
        // attacked a pet with an innocent owner
        else if (victim is Pet && ((Pet)victim).owner.IsInnocent())
        {
            // start offender if not a murderer yet
            if (!IsMurderer()) StartOffender();
        }
    }

    [Server]
    public void OnKilledEnemy(Entity victim)
    {
        // killed an innocent player
        if (victim is Player && ((Player)victim).IsInnocent())
        {
            StartMurderer();
        }
        // killed a pet with an innocent owner
        else if (victim is Pet && ((Pet)victim).owner.IsInnocent())
        {
            StartMurderer();
        }
    }

    // aggro ///////////////////////////////////////////////////////////////////
    // this function is called by entities that attack us
    [ServerCallback]
    public override void OnAggro(Entity entity)
    {
        // call base function
        base.OnAggro(entity);

        // forward to pet if it's supposed to defend us
        if (petControl.activePet != null && petControl.activePet.defendOwner)
            petControl.activePet.OnAggro(entity);
    }

    // movement ////////////////////////////////////////////////////////////////
    // check if movement is currently allowed
    // -> not in Movement.cs because we would have to add it to each player
    //    movement system. (can't use an abstract PlayerMovement.cs because
    //    PlayerNavMeshMovement needs to inherit from NavMeshMovement already)
    public bool IsMovementAllowed()
    {
        // some skills allow movement while casting
        bool castingAndAllowed = state == "CASTING" &&
                                 skills.currentSkill != -1 &&
                                 skills.skills[skills.currentSkill].allowMovement;

        // in a state where movement is allowed?
        // and if local player: not typing in an input?
        // (fix: only check for local player. checking in all cases means that
        //       no player could move if host types anything in an input)
        bool isLocalPlayerTyping = isLocalPlayer && UIUtils.AnyInputActive();
        return (state == "IDLE" || state == "MOVING" || castingAndAllowed) &&
               !isLocalPlayerTyping;
    }

    // death ///////////////////////////////////////////////////////////////////
    [Server]
    public override void OnDeath()
    {
        // take care of entity stuff
        base.OnDeath();

        // reset movement and navigation
        movement.Reset();
    }

    // item cooldowns //////////////////////////////////////////////////////////
    // get remaining item cooldown, or 0 if none
    public float GetItemCooldown(string cooldownCategory)
    {
        // find cooldown for that category
        if (itemCooldowns.TryGetValue(cooldownCategory, out double cooldownEnd))
        {
            return NetworkTime.time >= cooldownEnd ? 0 : (float)(cooldownEnd - NetworkTime.time);
        }

        // none found
        return 0;
    }

    // reset item cooldown
    public void SetItemCooldown(string cooldownCategory, float cooldown)
    {
        // save end time
        itemCooldowns[cooldownCategory] = NetworkTime.time + cooldown;
    }

    // attack //////////////////////////////////////////////////////////////////
    // CanAttack check
    // we use 'is' instead of 'GetType' so that it works for inherited types too
    public override bool CanAttack(Entity entity)
    {
        return base.CanAttack(entity) &&
               (entity is Monster ||
                entity is Player ||
                (entity is Pet && entity != petControl.activePet) ||
                (entity is Mount && entity != mountControl.activeMount));
    }

    // pvp murder system ///////////////////////////////////////////////////////
    // attacking someone innocent results in Offender status
    //   (can be attacked without penalty for a short time)
    // killing someone innocent results in Murderer status
    //   (can be attacked without penalty for a long time + negative buffs)
    // attacking/killing a Offender/Murderer has no penalty
    //
    // we use buffs for the offender/status because buffs have all the features
    // that we need here.
    //
    // NOTE: this is in Player.cs and not in PlayerCombat.cs for ease of use!
    public bool IsOffender()
    {
        return offenderBuff != null && skills.GetBuffIndexByName(offenderBuff.name) != -1;
    }

    public bool IsMurderer()
    {
        return murdererBuff != null && skills.GetBuffIndexByName(murdererBuff.name) != -1;
    }

    public bool IsInnocent()
    {
        return !IsOffender() && !IsMurderer();
    }

    public void StartOffender()
    {
        if (offenderBuff != null) skills.AddOrRefreshBuff(new Buff(offenderBuff, 1));
    }

    public void StartMurderer()
    {
        if (murdererBuff != null) skills.AddOrRefreshBuff(new Buff(murdererBuff, 1));
    }

    // selection handling //////////////////////////////////////////////////////
    [Command]
    public void CmdSetTarget(NetworkIdentity ni)
    {
        // validate
        if (ni != null)
        {
            // can directly change it, or change it after casting?
            if (state == "IDLE" || state == "MOVING" || state == "STUNNED")
                target = ni.GetComponent<Entity>();
            else if (state == "CASTING")
                nextTarget = ni.GetComponent<Entity>();
        }
    }

    // interaction /////////////////////////////////////////////////////////////
    protected override void OnInteract()
    {
        // not local player?
        if (this != localPlayer)
        {
            // attackable and has skills? => attack
            if (localPlayer.CanAttack(this) && localPlayer.skills.skills.Count > 0)
            {
                // then try to use that one
                ((PlayerSkills)localPlayer.skills).TryUse(0);
            }
            // otherwise just walk there
            // (e.g. if clicking on it in a safe zone where we can't attack)
            else
            {
                // use collider point(s) to also work with big entities
                Vector3 destination = Utils.ClosestPoint(this, localPlayer.transform.position);
                localPlayer.movement.Navigate(destination, localPlayer.interactionRange);
            }
        }
    }
}
