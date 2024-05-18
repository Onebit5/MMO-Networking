using UnityEngine;
using Mirror;
using TMPro;

[RequireComponent(typeof(Experience))]
[RequireComponent(typeof(PetSkills))]
[RequireComponent(typeof(NavMeshMovement))]
[RequireComponent(typeof(NetworkNavMeshAgent))]
public partial class Pet : Summonable
{
    [Header("Components")]
    public Experience experience;

    [Header("Icons")]
    public Sprite portraitIcon; // for pet status UI

    [Header("Text Meshes")]
    public TextMeshPro ownerNameOverlay;

    [Header("Movement")]
    public float returnDistance = 25; // return to player if dist > ...
    // pets should follow their targets even if they run out of the movement
    // radius. the follow dist should always be bigger than the biggest archer's
    // attack range, so that archers will always pull aggro, even when attacking
    // from far away.
    public float followDistance = 20;
    // pet should teleport if the owner gets too far away for whatever reason
    public float teleportDistance = 30;
    [Range(0.1f, 1)] public float attackToMoveRangeRatio = 0.8f; // move as close as 0.8 * attackRange to a target

    // use owner's speed if found, so that the pet can still follow the
    // owner if he is riding a mount, etc.
    public override float speed => owner != null ? owner.speed : base.speed;

    [Header("Death")]
    public float deathTime = 2; // enough for animation
    [HideInInspector] public double deathTimeEnd; // double for long term precision

    [Header("Behaviour")]
    [SyncVar] public bool defendOwner = true; // attack what attacks the owner
    [SyncVar] public bool autoAttack = true; // attack what the owner attacks

    // sync to item ////////////////////////////////////////////////////////////
    protected override ItemSlot SyncStateToItemSlot(ItemSlot slot)
    {
        // pet also has experience, unlike summonable. sync that too.
        slot = base.SyncStateToItemSlot(slot);
        slot.item.summonedExperience = experience.current;
        return slot;
    }

    // networkbehaviour ////////////////////////////////////////////////////////
    void LateUpdate()
    {
        // pass parameters to animation state machine
        // => passing the states directly is the most reliable way to avoid all
        //    kinds of glitches like movement sliding, attack twitching, etc.
        // => make sure to import all looping animations like idle/run/attack
        //    with 'loop time' enabled, otherwise the client might only play it
        //    once
        // => only play moving animation while actually moving (velocity). the
        //    MOVING state might be delayed to due latency or we might be in
        //    MOVING while a path is still pending, etc.
        // => skill names are assumed to be boolean parameters in animator
        //    so we don't need to worry about an animation number etc.
        if (isClient) // no need for animations on the server
        {
            animator.SetBool("MOVING", state == "MOVING" && movement.GetVelocity() != Vector3.zero);
            animator.SetBool("CASTING", state == "CASTING");
            animator.SetBool("STUNNED", state == "STUNNED");
            animator.SetBool("DEAD", state == "DEAD");
            foreach (Skill skill in skills.skills)
                animator.SetBool(skill.name, skill.CastTimeRemaining() > 0);
        }

        // update overlays in any case, except on server-only mode
        // (also update for character selection previews etc. then)
        if (!isServerOnly)
        {
            if (ownerNameOverlay != null)
            {
                if (owner != null)
                {
                    ownerNameOverlay.text = owner.name;
                    // find local player (null while in character selection)
                    if (Player.localPlayer != null)
                    {

                        // note: murderer has higher priority (a player can be a murderer and an
                        // offender at the same time)
                        if (owner.IsMurderer())
                            ownerNameOverlay.color = owner.nameOverlayMurdererColor;
                        else if (owner.IsOffender())
                            ownerNameOverlay.color = owner.nameOverlayOffenderColor;
                        // member of the same party
                        else if (Player.localPlayer.party.InParty() &&
                                 Player.localPlayer.party.party.Contains(owner.name))
                            ownerNameOverlay.color = owner.nameOverlayPartyColor;
                        // otherwise default
                        else
                            ownerNameOverlay.color = owner.nameOverlayDefaultColor;
                    }
                }
                else ownerNameOverlay.text = "?";
            }
        }
    }

    // OnDrawGizmos only happens while the Script is not collapsed
    void OnDrawGizmos()
    {
        // draw the movement area (around 'start' if game running,
        // or around current position if still editing)
        Vector3 startHelp = Application.isPlaying ? owner.petControl.petDestination : transform.position;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(startHelp, returnDistance);

        // draw the follow dist
        Gizmos.color = Color.gray;
        Gizmos.DrawWireSphere(startHelp, followDistance);
    }

    void OnDestroy()
    {
        // keep player's pet item up to date
        if (isServer)
            SyncToOwnerItem();
    }

    // always update pets. IsWorthUpdating otherwise only updates if has observers,
    // but pets should still be updated even if they are too far from any observers,
    // so that they continue to run to their owner.
    public override bool IsWorthUpdating() { return true; }

    // finite state machine events /////////////////////////////////////////////
    bool EventOwnerDisappeared() =>
        owner == null;

    bool EventDied() =>
        health.current == 0;

    bool EventDeathTimeElapsed() =>
        state == "DEAD" && NetworkTime.time >= deathTimeEnd;

    bool EventTargetDisappeared() =>
        target == null;

    bool EventTargetDied() =>
        target != null && target.health.current == 0;

    bool EventTargetTooFarToAttack() =>
        target != null &&
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count &&
        !skills.CastCheckDistance(skills.skills[skills.currentSkill], out Vector3 destination);

    bool EventTargetTooFarToFollow() =>
        target != null &&
        Vector3.Distance(owner.petControl.petDestination, target.collider.ClosestPointOnBounds(transform.position)) > followDistance;

    bool EventNeedReturnToOwner() =>
        Vector3.Distance(owner.petControl.petDestination, transform.position) > returnDistance;

    bool EventNeedTeleportToOwner() =>
        Vector3.Distance(owner.petControl.petDestination, transform.position) > teleportDistance;

    bool EventAggro() =>
        target != null && target.health.current > 0;

    bool EventSkillRequest() =>
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count;

    bool EventSkillFinished() =>
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count &&
               skills.skills[skills.currentSkill].CastTimeRemaining() == 0;

    bool EventMoveEnd() =>
        state == "MOVING" && !movement.IsMoving();

    bool EventStunned() =>
        NetworkTime.time <= stunTimeEnd;

    // finite state machine - server ///////////////////////////////////////////
    [Server]
    string UpdateServer_IDLE()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventOwnerDisappeared())
        {
            // owner might disconnect or get destroyed for some reason
            NetworkServer.Destroy(gameObject);
            return "IDLE";
        }
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
        if (EventTargetDied())
        {
            // we had a target before, but it died now. clear it.
            target = null;
            skills.CancelCast();
            return "IDLE";
        }
        if (EventNeedTeleportToOwner())
        {
            movement.Warp(owner.petControl.petDestination);
            return "IDLE";
        }
        if (EventNeedReturnToOwner())
        {
            // return to owner only while IDLE
            target = null;
            skills.CancelCast();
            movement.Navigate(owner.petControl.petDestination, 0);
            return "MOVING";
        }
        if (EventTargetTooFarToFollow())
        {
            // we had a target before, but it's out of follow range now.
            // clear it and go back to start. don't stay here.
            target = null;
            skills.CancelCast();
            movement.Navigate(owner.petControl.petDestination, 0);
            return "MOVING";
        }
        if (EventTargetTooFarToAttack())
        {
            // we had a target before, but it's out of attack range now.
            // follow it. (use collider point(s) to also work with big entities)
            float stoppingDistance = ((PetSkills)skills).CurrentCastRange() * attackToMoveRangeRatio;
            Vector3 destination = Utils.ClosestPoint(target, transform.position);
            movement.Navigate(destination, stoppingDistance);
            return "MOVING";
        }
        if (EventSkillRequest())
        {
            // we had a target in attack range before and trying to cast a skill
            // on it. check self (alive, mana, weapon etc.) and target
            Skill skill = skills.skills[skills.currentSkill];
            if (skills.CastCheckSelf(skill) && skills.CastCheckTarget(skill))
            {
                // start casting
                skills.StartCast(skill);
                return "CASTING";
            }
            else
            {
                // invalid target. reset attempted current skill cast.
                target = null;
                skills.currentSkill = -1;
                return "IDLE";
            }
        }
        if (EventAggro())
        {
            // target in attack range. try to cast a first skill on it
            if (skills.skills.Count > 0)
                skills.currentSkill = ((PetSkills)skills).NextSkill();
            else
                Debug.LogError(name + " has no skills to attack with.");
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventDeathTimeElapsed()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "IDLE"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_MOVING()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventOwnerDisappeared())
        {
            // owner might disconnect or get destroyed for some reason
            NetworkServer.Destroy(gameObject);
            return "IDLE";
        }
        if (EventDied())
        {
            // we died.
            movement.Reset();
            return "DEAD";
        }
        if (EventStunned())
        {
            movement.Reset();
            return "STUNNED";
        }
        if (EventMoveEnd())
        {
            // we reached our destination.
            return "IDLE";
        }
        if (EventTargetDied())
        {
            // we had a target before, but it died now. clear it.
            target = null;
            skills.CancelCast();
            movement.Reset();
            return "IDLE";
        }
        if (EventNeedTeleportToOwner())
        {
            movement.Warp(owner.petControl.petDestination);
            return "IDLE";
        }
        if (EventTargetTooFarToFollow())
        {
            // we had a target before, but it's out of follow range now.
            // clear it and go back to start. don't stay here.
            target = null;
            skills.CancelCast();
            movement.Navigate(owner.petControl.petDestination, 0);
            return "MOVING";
        }
        if (EventTargetTooFarToAttack())
        {
            // we had a target before, but it's out of attack range now.
            // follow it. (use collider point(s) to also work with big entities)
            float stoppingDistance = ((PetSkills)skills).CurrentCastRange() * attackToMoveRangeRatio;
            Vector3 destination = Utils.ClosestPoint(target, transform.position);
            movement.Navigate(destination, stoppingDistance);
            return "MOVING";
        }
        if (EventAggro())
        {
            // target in attack range. try to cast a first skill on it
            // (we may get a target while randomly wandering around)
            if (skills.skills.Count > 0)
                skills.currentSkill = ((PetSkills)skills).NextSkill();
            else
                Debug.LogError(name + " has no skills to attack with.");
            movement.Reset();
            return "IDLE";
        }
        if (EventNeedReturnToOwner()) {} // don't care
        if (EventDeathTimeElapsed()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventSkillRequest()) {} // don't care, finish movement first

        return "MOVING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_CASTING()
    {
        // keep looking at the target for server & clients (only Y rotation)
        if (target)
            movement.LookAtY(target.transform.position);

        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventOwnerDisappeared())
        {
            // owner might disconnect or get destroyed for some reason
            NetworkServer.Destroy(gameObject);
            return "IDLE";
        }
        if (EventDied())
        {
            // we died.
            return "DEAD";
        }
        if (EventStunned())
        {
            skills.CancelCast();
            movement.Reset();
            return "STUNNED";
        }
        if (EventTargetDisappeared())
        {
            // cancel if the target matters for this skill
            if (skills.skills[skills.currentSkill].cancelCastIfTargetDied)
            {
                skills.CancelCast();
                target = null;
                return "IDLE";
            }
        }
        if (EventTargetDied())
        {
            // cancel if the target matters for this skill
            if (skills.skills[skills.currentSkill].cancelCastIfTargetDied)
            {
                skills.CancelCast();
                target = null;
                return "IDLE";
            }
        }
        if (EventSkillFinished())
        {
            // finished casting. apply the skill on the target.
            skills.FinishCast(skills.skills[skills.currentSkill]);

            // did the target die? then clear it so that the monster doesn't
            // run towards it if the target respawned
            if (target.health.current == 0) target = null;

            // go back to IDLE. reset current skill.
            ((PetSkills)skills).lastSkill = skills.currentSkill;
            skills.currentSkill = -1;
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventDeathTimeElapsed()) {} // don't care
        if (EventNeedTeleportToOwner()) {} // don't care
        if (EventNeedReturnToOwner()) {} // don't care
        if (EventTargetTooFarToAttack()) {} // don't care, we were close enough when starting to cast
        if (EventTargetTooFarToFollow()) {} // don't care, we were close enough when starting to cast
        if (EventAggro()) {} // don't care, always have aggro while casting
        if (EventSkillRequest()) {} // don't care, that's why we are here

        return "CASTING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_STUNNED()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventOwnerDisappeared())
        {
            // owner might disconnect or get destroyed for some reason
            NetworkServer.Destroy(gameObject);
            return "IDLE";
        }
        if (EventDied())
        {
            // we died.
            skills.CancelCast(); // in case we died while trying to cast
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
    string UpdateServer_DEAD()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventOwnerDisappeared())
        {
            // owner might disconnect or get destroyed for some reason
            NetworkServer.Destroy(gameObject);
            return "DEAD";
        }
        if (EventDeathTimeElapsed())
        {
            // we were lying around dead for long enough now.
            // hide while respawning, or disappear forever
            NetworkServer.Destroy(gameObject);
            return "DEAD";
        }
        if (EventSkillRequest()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventNeedTeleportToOwner()) {} // don't care
        if (EventNeedReturnToOwner()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetTooFarToFollow()) {} // don't care
        if (EventTargetTooFarToAttack()) {} // don't care
        if (EventAggro()) {} // don't care
        if (EventDied()) {} // don't care, of course we are dead

        return "DEAD"; // nothing interesting happened
    }

    [Server]
    protected override string UpdateServer()
    {
        if (state == "IDLE")    return UpdateServer_IDLE();
        if (state == "MOVING")  return UpdateServer_MOVING();
        if (state == "CASTING") return UpdateServer_CASTING();
        if (state == "STUNNED") return UpdateServer_STUNNED();
        if (state == "DEAD")    return UpdateServer_DEAD();
        Debug.LogError("invalid state:" + state);
        return "IDLE";
    }

    // finite state machine - client ///////////////////////////////////////////
    [Client]
    protected override void UpdateClient() {}

    // aggro ///////////////////////////////////////////////////////////////////
    // this function is called by entities that attack us
    [ServerCallback]
    public override void OnAggro(Entity entity)
    {
        // call base function
        base.OnAggro(entity);

        // are we alive, and is the entity alive and of correct type?
        if (CanAttack(entity))
        {
            // no target yet(==self), or closer than current target?
            // => has to be at least 20% closer to be worth it, otherwise we
            //    may end up nervously switching between two targets
            // => we do NOT use Utils.ClosestDistance, because then we often
            //    also end up nervously switching between two animated targets,
            //    since their collides moves with the animation.
            if (target == null)
            {
                target = entity;
            }
            else
            {
                float oldDistance = Vector3.Distance(transform.position, target.transform.position);
                float newDistance = Vector3.Distance(transform.position, entity.transform.position);
                if (newDistance < oldDistance * 0.8) target = entity;
            }
        }
    }

    // death ///////////////////////////////////////////////////////////////////
    [Server]
    public override void OnDeath()
    {
        // take care of entity stuff
        base.OnDeath();

        // set death end time. we set it now to make sure that everything works
        // fine even if a pet isn't updated for a while. so as soon as it's
        // updated again, the death/respawn will happen immediately if current
        // time > end time.
        deathTimeEnd = NetworkTime.time + deathTime;

        // keep player's pet item up to date
        SyncToOwnerItem();
    }

    // attack //////////////////////////////////////////////////////////////////
    // CanAttack check
    // we use 'is' instead of 'GetType' so that it works for inherited types too
    public override bool CanAttack(Entity entity)
    {
        return base.CanAttack(entity) &&
               (entity is Monster ||
                (entity is Player && entity != owner) ||
                (entity is Pet pet && pet.owner != owner) ||
                (entity is Mount mount && mount.owner != owner));
    }

    // owner controls //////////////////////////////////////////////////////////
    // the Commands can only be called by the owner connection, so make sure to
    // spawn the pet via NetworkServer.Spawn(prefab, ownerConnection).
    [Command]
    public void CmdSetAutoAttack(bool value)
    {
        autoAttack = value;
    }

    [Command]
    public void CmdSetDefendOwner(bool value)
    {
        defendOwner = value;
    }

    // interaction /////////////////////////////////////////////////////////////
    protected override void OnInteract()
    {
        Player player = Player.localPlayer;

        // not local player's pet?
        if (this != player.petControl.activePet)
        {
            // attackable and has skills? => attack
            if (player.CanAttack(this) && player.skills.skills.Count > 0)
            {
                // then try to use that one
                ((PlayerSkills)player.skills).TryUse(0);
            }
            // otherwise just walk there
            // (e.g. if clicking on it in a safe zone where we can't attack)
            else
            {
                // use collider point(s) to also work with big entities
                Vector3 destination = Utils.ClosestPoint(this, player.transform.position);
                player.movement.Navigate(destination, player.interactionRange);
            }
        }
    }
}
