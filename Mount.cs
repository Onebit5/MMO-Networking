using UnityEngine;
using Mirror;

public partial class Mount : Summonable
{
    [Header("Death")]
    public float deathTime = 2; // enough for animation
    [HideInInspector] public double deathTimeEnd; // double for long term precision

    [Header("Seat Position")]
    public Transform seat;

    // networkbehaviour ////////////////////////////////////////////////////////
    void LateUpdate()
    {
        // pass parameters to animation state machine
        // => passing the states directly is the most reliable way to avoid all
        //    kinds of glitches like movement sliding, attack twitching, etc.
        // => make sure to import all looping animations like idle/run/attack
        //    with 'loop time' enabled, otherwise the client might only play it
        //    once
        // => only play moving animation while the owner is actually moving. the
        //    MOVING state might be delayed to due latency or we might be in
        //    MOVING while a path is still pending, etc.
        if (isClient) // no need for animations on the server
        {
            // use owner's moving state for maximum precision (not if dead)
            // (if owner spawn reached us yet)
            animator.SetBool("MOVING", health.current > 0 && owner != null && owner.movement.IsMoving());
            animator.SetBool("DEAD", state == "DEAD");
        }
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
    public override bool IsWorthUpdating() => true;

    // finite state machine events /////////////////////////////////////////////
    bool EventOwnerDisappeared()=>
        owner == null;

    bool EventOwnerDied() =>
        owner != null && owner.health.current == 0;

    bool EventDied() =>
        health.current == 0;

    bool EventDeathTimeElapsed() =>
        state == "DEAD" && NetworkTime.time >= deathTimeEnd;

    // finite state machine - server ///////////////////////////////////////////
    // copy owner's position and rotation. no need for NetworkTransform.
    void CopyOwnerPositionAndRotation()
    {
        if (owner != null)
        {
            // mount doesn't need a movement system with a .Warp function.
            // we simply copy the owner position/rotation at all times.
            transform.position = owner.transform.position;
            transform.rotation = owner.transform.rotation;
        }
    }

    [Server]
    string UpdateServer_IDLE()
    {
        // copy owner's position and rotation. no need for NetworkTransform.
        CopyOwnerPositionAndRotation();

        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventOwnerDisappeared())
        {
            // owner might disconnect or get destroyed for some reason
            NetworkServer.Destroy(gameObject);
            return "IDLE";
        }
        if (EventOwnerDied())
        {
            // die if owner died, so the mount doesn't stand around there forever
            health.current = 0;
        }
        if (EventDied())
        {
            // we died.
            return "DEAD";
        }
        if (EventDeathTimeElapsed()) {} // don't care

        return "IDLE"; // nothing interesting happened
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
        if (EventOwnerDied()) {} // don't care
        if (EventDied()) {} // don't care, of course we are dead

        return "DEAD"; // nothing interesting happened
    }

    [Server]
    protected override string UpdateServer()
    {
        if (state == "IDLE")    return UpdateServer_IDLE();
        if (state == "DEAD")    return UpdateServer_DEAD();
        Debug.LogError("invalid state:" + state);
        return "IDLE";
    }

    // finite state machine - client ///////////////////////////////////////////
    [Client]
    protected override void UpdateClient()
    {
        if (state == "IDLE" || state == "MOVING")
        {
            // copy owner's position and rotation. no need for NetworkTransform.
            CopyOwnerPositionAndRotation();
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
    public override bool CanAttack(Entity entity) { return false; }

    // interaction /////////////////////////////////////////////////////////////
    protected override void OnInteract()
    {
        Player player = Player.localPlayer;

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
