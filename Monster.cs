// The Monster class has a few different features that all aim to make monsters
// behave as realistically as possible.
//
// - **States:** first of all, the monster has several different states like
// IDLE, ATTACKING, MOVING and DEATH. The monster will randomly move around in
// a certain movement radius and try to attack any players in its aggro range.
//
// - **Aggro:** To save computations, we let Unity take care of finding players
// in the aggro range by simply adding a AggroArea _(see AggroArea.cs)_ sphere
// to the monster's children in the Hierarchy. We then use the OnTrigger
// functions to find players that are in the aggro area. The monster will always
// move to the nearest aggro player and then attack it as long as the player is
// in the follow radius. If the player happens to walk out of the follow
// radius then the monster will walk back to the start position quickly.
//
// - **Respawning:** The monsters have a _respawn_ property that can be set to
// true in order to make the monster respawn after it died. We developed the
// respawn system with simplicity in mind, there are no extra spawner objects
// needed. As soon as a monster dies, it will make itself invisible for a while
// and then go back to the starting position to respawn. This feature allows the
// developer to quickly drag monster Prefabs into the scene and place them
// anywhere, without worrying about spawners and spawn areas.
//
// - **Loot:** Dead monsters can also generate loot, based on the _lootItems_
// list. Each monster has a list of items with their dropchance, so that loot
// will always be generated randomly. Monsters can also randomly generate loot
// gold between a minimum and a maximum amount.
using UnityEngine;
using Mirror;

[RequireComponent(typeof(Inventory))]
[RequireComponent(typeof(MonsterSkills))]
[RequireComponent(typeof(NavMeshMovement))]
[RequireComponent(typeof(NetworkNavMeshAgent))]
public partial class Monster : Entity
{
    [Header("Components")]
    public MonsterInventory inventory;

    [Header("Movement")]
    [Range(0, 1)] public float moveProbability = 0.1f; // chance per second
    public float moveDistance = 10;
    // monsters should follow their targets even if they run out of the movement
    // radius. the follow dist should always be bigger than the biggest archer's
    // attack range, so that archers will always pull aggro, even when attacking
    // from far away.
    public float followDistance = 20;
    [Range(0.1f, 1)] public float attackToMoveRangeRatio = 0.8f; // move as close as 0.8 * attackRange to a target

    [Header("Experience Reward")]
    public long rewardExperience = 10;
    public long rewardSkillExperience = 2;

    [Header("Respawn")]
    public float deathTime = 30f; // enough for animation & looting
    [HideInInspector] public double deathTimeEnd; // double for long term precision
    public bool respawn = true;
    public float respawnTime = 10f;
    [HideInInspector] public double respawnTimeEnd; // double for long term precision

    // save the start position for random movement distance and respawning
    [HideInInspector] public Vector3 startPosition;

    // networkbehaviour ////////////////////////////////////////////////////////
    protected override void Start()
    {
        base.Start();

        // remember start position in case we need to respawn later
        startPosition = transform.position;
    }

    void LateUpdate()
    {
        // pass parameters to animation state machine
        // => passing the states directly is the most reliable way to avoid all
        //    kinds of glitches like movement sliding, attack twitching, etc.
        // => make sure to import all looping animations like idle/run/attack
        //    with 'loop time' enabled, otherwise the client might only play it
        //    once
        // => only play moving animation while the actually moving (velocity).
        //    the MOVING state might be delayed to due latency or we might be in
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
    }

    // finite state machine events /////////////////////////////////////////////
    bool EventDied() =>
        health.current == 0;

    bool EventDeathTimeElapsed() =>
        state == "DEAD" && NetworkTime.time >= deathTimeEnd;

    bool EventRespawnTimeElapsed() =>
        state == "DEAD" && respawn && NetworkTime.time >= respawnTimeEnd;

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
        Vector3.Distance(startPosition, target.collider.ClosestPointOnBounds(transform.position)) > followDistance;

    bool EventTargetEnteredSafeZone() =>
        target != null && target.inSafeZone;

    bool EventAggro() =>
        target != null && target.health.current > 0;

    bool EventSkillRequest() =>
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count;

    bool EventSkillFinished() =>
        0 <= skills.currentSkill && skills.currentSkill < skills.skills.Count &&
        skills.skills[skills.currentSkill].CastTimeRemaining() == 0;

    bool EventMoveEnd() =>
        state == "MOVING" && !movement.IsMoving();

    bool EventMoveRandomly() =>
        Random.value <= moveProbability * Time.deltaTime;

    bool EventStunned() =>
        NetworkTime.time <= stunTimeEnd;

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
        if (EventTargetDied())
        {
            // we had a target before, but it died now. clear it.
            target = null;
            skills.CancelCast();
            return "IDLE";
        }
        if (EventTargetTooFarToFollow())
        {
            // we had a target before, but it's out of follow range now.
            // clear it and go back to start. don't stay here.
            target = null;
            skills.CancelCast();
            movement.Navigate(startPosition, 0);
            return "MOVING";
        }
        if (EventTargetTooFarToAttack())
        {
            // we had a target before, but it's out of attack range now.
            // follow it. (use collider point(s) to also work with big entities)
            float stoppingDistance = ((MonsterSkills)skills).CurrentCastRange() * attackToMoveRangeRatio;
            Vector3 destination = Utils.ClosestPoint(target, transform.position);
            movement.Navigate(destination, stoppingDistance);
            return "MOVING";
        }
        if (EventTargetEnteredSafeZone())
        {
            // if our target entered the safe zone, we need to be really careful
            // to avoid kiting.
            // -> players could pull a monster near a safe zone and then step in
            //    and out of it before/after attacks without ever getting hit by
            //    the monster
            // -> running back to start won't help, can still kit while running
            // -> warping back to start won't help, we might accidentally placed
            //    a monster in attack range of a safe zone
            // -> the 100% secure way is to die and hide it immediately. many
            //    popular MMOs do it the same way to avoid exploits.
            // => call Entity.OnDeath without rewards etc. and hide immediately
            base.OnDeath(); // no looting
            respawnTimeEnd = NetworkTime.time + respawnTime; // respawn in a while
            return "DEAD";
        }
        if (EventSkillRequest())
        {
            // we had a target in attack range before and trying to cast a skill
            // on it. check self (alive, mana, weapon etc.) and target
            Skill skill = skills.skills[skills.currentSkill];
            if (skills.CastCheckSelf(skill))
            {
                if (skills.CastCheckTarget(skill))
                {
                    // start casting
                    skills.StartCast(skill);
                    return "CASTING";
                }
                else
                {
                    // invalid target. clear the attempted current skill.
                    target = null;
                    skills.currentSkill = -1;
                    return "IDLE";
                }
            }
            else
            {
                // we can't cast this skill at the moment (cooldown/low mana/...)
                // -> clear the attempted current skill, but keep the target to
                // continue later
                skills.currentSkill = -1;
                return "IDLE";
            }
        }
        if (EventAggro())
        {
            // target in attack range. try to cast a first skill on it
            if (skills.skills.Count > 0)
                skills.currentSkill = ((MonsterSkills)skills).NextSkill();
            else
                Debug.LogError(name + " has no skills to attack with.");
            return "IDLE";
        }
        if (EventMoveRandomly())
        {
            // walk to a random position in movement radius (from 'start')
            // note: circle y is 0 because we add it to start.y
            Vector3 circle2D = Random.insideUnitCircle * moveDistance;
            movement.Navigate(startPosition + new Vector3(circle2D.x, 0, circle2D.y), 0);
            return "MOVING";
        }
        if (EventDeathTimeElapsed()) {} // don't care
        if (EventRespawnTimeElapsed()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
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
        if (EventTargetTooFarToFollow())
        {
            // we had a target before, but it's out of follow range now.
            // clear it and go back to start. don't stay here.
            target = null;
            skills.CancelCast();
            movement.Navigate(startPosition, 0);
            return "MOVING";
        }
        if (EventTargetTooFarToAttack())
        {
            // we had a target before, but it's out of attack range now.
            // follow it. (use collider point(s) to also work with big entities)
            float stoppingDistance = ((MonsterSkills)skills).CurrentCastRange() * attackToMoveRangeRatio;
            Vector3 destination = Utils.ClosestPoint(target, transform.position);
            movement.Navigate(destination, stoppingDistance);
            return "MOVING";
        }
        if (EventTargetEnteredSafeZone())
        {
            // if our target entered the safe zone, we need to be really careful
            // to avoid kiting.
            // -> players could pull a monster near a safe zone and then step in
            //    and out of it before/after attacks without ever getting hit by
            //    the monster
            // -> running back to start won't help, can still kit while running
            // -> warping back to start won't help, we might accidentally placed
            //    a monster in attack range of a safe zone
            // -> the 100% secure way is to die and hide it immediately. many
            //    popular MMOs do it the same way to avoid exploits.
            // => call Entity.OnDeath without rewards etc. and hide immediately
            base.OnDeath(); // no looting
            respawnTimeEnd = NetworkTime.time + respawnTime; // respawn in a while
            return "DEAD";
        }
        if (EventAggro())
        {
            // target in attack range. try to cast a first skill on it
            // (we may get a target while randomly wandering around)
            if (skills.skills.Count > 0)
                skills.currentSkill = ((MonsterSkills)skills).NextSkill();
            else
                Debug.LogError(name + " has no skills to attack with.");
            movement.Reset();
            return "IDLE";
        }
        if (EventDeathTimeElapsed()) {} // don't care
        if (EventRespawnTimeElapsed()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventSkillRequest()) {} // don't care, finish movement first
        if (EventMoveRandomly()) {} // don't care

        return "MOVING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_CASTING()
    {
        // keep looking at the target for server & clients (only Y rotation)
        if (target)
            movement.LookAtY(target.transform.position);

        // events sorted by priority (e.g. target doesn't matter if we died)
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
        if (EventTargetEnteredSafeZone())
        {
            // cancel if the target matters for this skill
            if (skills.skills[skills.currentSkill].cancelCastIfTargetDied)
            {
                // if our target entered the safe zone, we need to be really careful
                // to avoid kiting.
                // -> players could pull a monster near a safe zone and then step in
                //    and out of it before/after attacks without ever getting hit by
                //    the monster
                // -> running back to start won't help, can still kit while running
                // -> warping back to start won't help, we might accidentally placed
                //    a monster in attack range of a safe zone
                // -> the 100% secure way is to die and hide it immediately. many
                //    popular MMOs do it the same way to avoid exploits.
                // => call Entity.OnDeath without rewards etc. and hide immediately
                base.OnDeath(); // no looting
                respawnTimeEnd = NetworkTime.time + respawnTime; // respawn in a while
                return "DEAD";
            }
        }
        if (EventSkillFinished())
        {
            // finished casting. apply the skill on the target.
            skills.FinishCast(skills.skills[skills.currentSkill]);

            // did the target die? then clear it so that the monster doesn't
            // run towards it if the target respawned
            // (target might be null if disappeared or targetless skill)
            if (target != null && target.health.current == 0)
                target = null;

            // go back to IDLE, reset current skill
            ((MonsterSkills)skills).lastSkill = skills.currentSkill;
            skills.currentSkill = -1;
            return "IDLE";
        }
        if (EventDeathTimeElapsed()) {} // don't care
        if (EventRespawnTimeElapsed()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventTargetTooFarToAttack()) {} // don't care, we were close enough when starting to cast
        if (EventTargetTooFarToFollow()) {} // don't care, we were close enough when starting to cast
        if (EventAggro()) {} // don't care, always have aggro while casting
        if (EventSkillRequest()) {} // don't care, that's why we are here
        if (EventMoveRandomly()) {} // don't care

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
    string UpdateServer_DEAD()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventRespawnTimeElapsed())
        {
            // respawn at the start position with full health, visibility, no loot
            gold = 0;
            inventory.slots.Clear();
            Show();
            // warp to new position (never use transform.position for agents!)
            //
            // NOTE: Warp sends RpcWarp to clients automatically, but the
            //       monster has 0 observers since it was hidden until now.
            //       SpawnMessage -> NetworkNavMeshAgent.OnDeserialize has an
            //       'if initialState then Warp' check which moves it on clients.
            movement.Warp(startPosition);
            Revive();
            return "IDLE";
        }
        if (EventDeathTimeElapsed())
        {
            // we were lying around dead for long enough now.
            // hide while respawning, or disappear forever
            if (respawn) Hide();
            else NetworkServer.Destroy(gameObject);
            return "DEAD";
        }
        if (EventSkillRequest()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetTooFarToFollow()) {} // don't care
        if (EventTargetTooFarToAttack()) {} // don't care
        if (EventTargetEnteredSafeZone()) {} // don't care
        if (EventAggro()) {} // don't care
        if (EventMoveRandomly()) {} // don't care
        if (EventStunned()) {} // don't care
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
    protected override void UpdateClient()
    {
        if (state == "CASTING")
        {
            // keep looking at the target for server & clients (only Y rotation)
            if (target)
                movement.LookAtY(target.transform.position);
        }
    }

    // DrawGizmos can be used for debug info
    public void OnDrawGizmos()
    {
        // draw the movement area (around 'start' if game running,
        // or around current position if still editing)
        Vector3 startHelp = Application.isPlaying ? startPosition : transform.position;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(startHelp, moveDistance);

        // draw the follow dist
        Gizmos.color = Color.gray;
        Gizmos.DrawWireSphere(startHelp, followDistance);
    }

    // aggro ///////////////////////////////////////////////////////////////////
    // this function is called by entities that attack us and by AggroArea
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
            //    => we don't even need closestdistance here because they are in
            //       the aggro area anyway. transform.position is perfectly fine
            if (target == null)
            {
                target = entity;
            }
            else if (entity != target) // no need to check dist for same target
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

        // set death and respawn end times. we set both of them now to make sure
        // that everything works fine even if a monster isn't updated for a
        // while. so as soon as it's updated again, the death/respawn will
        // happen immediately if current time > end time.
        deathTimeEnd = NetworkTime.time + deathTime;
        respawnTimeEnd = deathTimeEnd + respawnTime; // after death time ended
    }

    // attack //////////////////////////////////////////////////////////////////
    // CanAttack check
    // we use 'is' instead of 'GetType' so that it works for inherited types too
    public override bool CanAttack(Entity entity)
    {
        return base.CanAttack(entity) &&
               (entity is Player ||
                entity is Pet ||
                entity is Mount);
    }

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
        // dead, has loot, close enough?
        // use collider point(s) to also work with big entities
        else if (health.current == 0 &&
                 Utils.ClosestDistance(player, this) <= player.interactionRange &&
                 inventory.HasLoot())
        {
            UILoot.singleton.Show();
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
