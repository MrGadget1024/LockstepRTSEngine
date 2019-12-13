﻿using Newtonsoft.Json;
using RTSLockstep.Grid;
using System;
using UnityEngine;

namespace RTSLockstep
{
    [DisallowMultipleComponent]
    public class Construct : ActiveAbility
    {
        #region Properties
        public ConstructGroup MyConstructGroup;
        [HideInInspector]
        public int MyConstructGroupID;

        public bool IsBuildMoving { get; private set; }

        public string[] BuildActions
        {
            get { return _buildActions; }
        }

        //Called whenever construction is stopped... i.e. to attack
        public event Action OnStopConstruct;

        public RTSAgent CurrentProject;
        private Structure ProjectStructure
        {
            get
            {
                return CurrentProject.IsNotNull() ? CurrentProject.GetAbility<Structure>() : null;
            }
        }

        private const int searchRate = LockstepManager.FrameRate / 2;

        //Stuff for the logic
        private bool inRange;
        private Vector2d targetDirection;
        private long fastMag;
        private long fastRangeToTarget;

        private int basePriority;
        private uint targetVersion;
        private long constructCount;

        private int loadedProjectId = -1;

        [Lockstep(true)]
        private bool IsWindingUp { get; set; }
        private long windupCount;

        protected virtual AnimState ConstructingAnimState
        {
            get { return AnimState.Constructing; }
        }

        #region variables for quick fix for repathing to target's new position
        private const long repathDistance = FixedMath.One * 2;
        private FrameTimer repathTimer = new FrameTimer();
        private const int repathInterval = LockstepManager.FrameRate * 2;
        private int repathRandom = 0;
        #endregion

        #region Serialized Values (Further description in properties)
        [SerializeField, FixedNumber]
        private long constructAmount = FixedMath.One;
        [SerializeField, FixedNumber, Tooltip("Used to determine how fast agent can build.")]
        private long _constructionSpeed = 1 * FixedMath.One;
        [SerializeField, Tooltip("Enter object names for prefabs this agent can build.")]
        private string[] _buildActions;
        [SerializeField, FixedNumber]
        private long _windup = 0;
        [SerializeField]
        private bool _increasePriority = true;
        #endregion
        #endregion Properties

        protected override void OnSetup()
        {
            basePriority = Agent.Body.Priority;
        }

        protected override void OnInitialize()
        {
            constructCount = 0;

            IsBuildMoving = false;

            MyConstructGroup = null;
            MyConstructGroupID = -1;

            CurrentProject = null;

            inRange = false;
            IsFocused = false;

            if (Agent.MyStats.CanMove)
            {
                Agent.MyStats.CachedMove.OnArrive += HandleOnArrive;
            }

            repathTimer.Reset(repathInterval);
            repathRandom = LSUtility.GetRandom(repathInterval);

            // need to move this to a construct group
            if (Agent.GetCommander() && loadedSavedValues && loadedProjectId >= 0)
            {
                RTSAgent obj = Agent.GetCommander().GetObjectForId(loadedProjectId);
                if (obj.MyAgentType == AgentType.Structure)
                {
                    CurrentProject = obj;
                }
            }
        }

        protected override void OnSimulate()
        {
            if (Agent.Tag == AgentTag.Builder)
            {
                if (constructCount > _constructionSpeed)
                {
                    //reset constructCount overcharge if left idle
                    constructCount = _constructionSpeed;
                }
                else if (constructCount < _constructionSpeed)
                {
                    //charge up constructCount
                    constructCount += LockstepManager.DeltaTime;
                }

                if (Agent && Agent.IsActive)
                {
                    if ((IsFocused || IsBuildMoving))
                    {
                        BehaveWithTarget();
                    }
                }

                if (Agent.MyStats.CanMove && IsBuildMoving)
                {
                    Agent.MyStats.CachedMove.StartLookingForStopPause();
                }
            }
        }

        protected override void OnExecute(Command com)
        {
                Agent.StopCast(ID);
                IsCasting = true;
                RegisterConstructGroup();
        }

        protected virtual void OnStartConstructMove()
        {
            if (Agent.MyStats.CanMove
                && ProjectStructure.IsNotNull()
                && !CheckRange())
            {
                IsBuildMoving = true;
                IsFocused = false;

                Agent.MyStats.CachedMove.StartMove(CurrentProject.Body.Position);
            }
        }

        protected virtual void OnConstruct(Structure target)
        {
            if (target.NeedsConstruction)
            {
                target.BuildUp(constructAmount);

                //if (audioElement != null)
                //{
                //    audioElement.Play(finishedJobSound);
                //}
            }
            else
            {
                // what are we building for then?
                StopConstruction();
            }
        }

        protected override void OnDeactivate()
        {
            StopConstruction(true);
        }

        protected sealed override void OnStopCast()
        {
            StopConstruction(true);
        }

        protected override void OnSaveDetails(JsonWriter writer)
        {
            SaveDetails(writer);
            SaveManager.WriteBoolean(writer, "BuildMoving", IsBuildMoving);
            if (ProjectStructure)
            {
                SaveManager.WriteInt(writer, "currentProjectId", CurrentProject.GlobalID);
            }
            SaveManager.WriteBoolean(writer, "Focused", IsFocused);
            SaveManager.WriteBoolean(writer, "InRange", inRange);
            SaveManager.WriteLong(writer, "ConstructCount", constructCount);
            SaveManager.WriteLong(writer, "FastRangeToTarget", fastRangeToTarget);
        }

        protected override void OnLoadProperty(JsonTextReader reader, string propertyName, object readValue)
        {
            base.OnLoadProperty(reader, propertyName, readValue);
            switch (propertyName)
            {
                case "BuildMoving":
                    IsBuildMoving = (bool)readValue;
                    break;
                case "currentProjectId":
                    loadedProjectId = (int)(long)readValue;
                    break;
                case "Focused":
                    IsFocused = (bool)readValue;
                    break;
                case "InRange":
                    inRange = (bool)readValue;
                    break;
                case "ConstructCount":
                    constructCount = (long)readValue;
                    break;
                case "FastRangeToTarget":
                    fastRangeToTarget = (long)readValue;
                    break;
                default:
                    break;
            }
        }

        public void OnConstructGroupProcessed(RTSAgent currentProject)
        {
            Agent.Tag = AgentTag.Builder;

            CurrentProject = currentProject;

            IsFocused = true;
            IsBuildMoving = false;

            targetVersion = CurrentProject.SpawnVersion;

            fastRangeToTarget = Agent.MyStats.ActionRange + (CurrentProject.Body.IsNotNull() ? CurrentProject.Body.Radius : 0) + Agent.Body.Radius;
            fastRangeToTarget *= fastRangeToTarget;
        }

        private void RegisterConstructGroup()
        {
            if (ConstructionGroupHelper.CheckValidAndAlert())
            {
                ConstructionGroupHelper.LastCreatedGroup.Add(this);
            }
        }

        private void HandleOnArrive()
        {
            if (IsBuildMoving)
            {
                IsFocused = true;
                IsBuildMoving = false;
            }
        }

        private void BehaveWithTarget()
        {
            // only stop construct when groups queue is empty
            if (!CurrentProject.IsActive
                || CurrentProject.SpawnVersion != targetVersion
                || !ProjectStructure.NeedsConstruction && MyConstructGroup.ConstructionQueue.Count == 0)
            {
                // Target's lifecycle has ended
                StopConstruction();
            }
            else
            {
                if (!IsWindingUp)
                {
                    if (CheckRange())
                    {
                        if (!inRange)
                        {
                            if (Agent.MyStats.CanMove)
                            {
                                Agent.MyStats.CachedMove.Arrive();
                            }

                            inRange = true;
                        }
                        Agent.Animator.SetState(ConstructingAnimState);

                        if (!ProjectStructure.ConstructionStarted)
                        {
                            ProjectStructure.ConstructionStarted = true;

                            if (CurrentProject.Animator.IsNotNull())
                            {
                                CurrentProject.Animator.SetState(AnimState.Building);
                            }

                            // Restore material
                            ConstructionHandler.RestoreMaterial(CurrentProject.gameObject);

                            // restore bounds so structure is included in path & build grid
                            if (CurrentProject.GetAbility<DynamicBlocker>())
                            {
                                CurrentProject.GetAbility<DynamicBlocker>().SetTransparent(false);
                            }
                        }

                        targetDirection.Normalize(out long mag);
                        bool withinTurn = Agent.MyStats.CachedAttack.TrackAttackAngle == false ||
                                          (fastMag != 0 &&
                                          Agent.Body.Forward.Dot(targetDirection.x, targetDirection.y) > 0
                                          && Agent.Body.Forward.Cross(targetDirection.x, targetDirection.y).Abs() <= Agent.MyStats.CachedAttack.AttackAngle);

                        bool needTurn = mag != 0 && !withinTurn;
                        if (needTurn && Agent.MyStats.CanTurn)
                        {
                            Agent.MyStats.CachedTurn.StartTurnDirection(targetDirection);
                        }
                        else if (constructCount >= _constructionSpeed)
                        {
                            StartWindup();
                        }
                    }
                    else
                    {
                        if (Agent.MyStats.CanMove)
                        {
                            Agent.MyStats.CachedMove.PauseAutoStop();
                            Agent.MyStats.CachedMove.PauseCollisionStop();
                            if (!Agent.MyStats.CachedMove.IsMoving
                                && !Agent.MyStats.CachedMove.MoveOnGroupProcessed)
                            {
                                OnStartConstructMove();
                                Agent.Body.Priority = basePriority;
                            }
                            else
                            {
                                if (inRange)
                                {
                                    Agent.MyStats.CachedMove.Destination = CurrentProject.Body.Position;
                                }
                                else
                                {
                                    if (repathTimer.AdvanceFrame())
                                    {
                                        if (CurrentProject.Body.PositionChangedBuffer &&
                                            CurrentProject.Body.Position.FastDistance(Agent.MyStats.CachedMove.Destination.x, Agent.MyStats.CachedMove.Destination.y) >= (repathDistance * repathDistance))
                                        {
                                            OnStartConstructMove();
                                            //So units don't sync up and path on the same frame
                                            repathTimer.AdvanceFrames(repathRandom);
                                        }
                                    }
                                }
                            }
                        }

                        if (inRange)
                        {
                            inRange = false;
                        }
                    }
                }

                if (IsWindingUp)
                {
                    //TODO: Do we need AgentConditional checks here?
                    windupCount += LockstepManager.DeltaTime;
                    if (Agent.MyStats.CanTurn)
                    {
                        Vector2d targetVector = CurrentProject.Body.Position - Agent.Body.Position;
                        Agent.MyStats.CachedTurn.StartTurnVector(targetVector);
                    }

                    if (windupCount >= _windup)
                    {
                        windupCount = 0;
                        StartConstruction();
                        while (constructCount >= _constructionSpeed)
                        {
                            //resetting back down after attack is fired
                            constructCount -= _constructionSpeed;
                        }
                        constructCount += _windup;
                        IsWindingUp = false;
                    }
                }
                else
                {
                    windupCount = 0;
                }

                if (Agent.MyStats.CanMove && inRange)
                {
                    Agent.MyStats.CachedMove.PauseAutoStop();
                    Agent.MyStats.CachedMove.PauseCollisionStop();
                }
            }
        }

        private bool CheckRange()
        {
            targetDirection = CurrentProject.Body.Position - Agent.Body.Position;
            fastMag = targetDirection.FastMagnitude();

            return fastMag <= fastRangeToTarget;
        }

        private void StartWindup()
        {
            windupCount = 0;
            IsWindingUp = true;
        }

        private void StartConstruction()
        {
            if (Agent.MyStats.CanMove)
            {
                // we don't want to be able to construct and move!
                IsBuildMoving = false;
                Agent.MyStats.CachedMove.StopMove();
            }
            Agent.Body.Priority = _increasePriority ? basePriority + 1 : basePriority;

            OnConstruct(ProjectStructure);
        }

        private void StopConstruction(bool complete = false)
        {
            inRange = false;
            IsWindingUp = false;
            IsFocused = false;

            if (MyConstructGroup.IsNotNull()
                && MyConstructGroup.ConstructionQueue.Count == 0)
            {
                MyConstructGroup.Remove(this);
            }

            IsBuildMoving = false;

            if (complete)
            {
                Agent.Tag = AgentTag.None;
            }
            else if (CurrentProject.IsNotNull())
            {
                if (Agent.MyStats.CanMove && !inRange)
                {
                    Agent.MyStats.CachedMove.StopMove();
                }
            }

            CurrentProject = null;

            IsCasting = false;

            Agent.Body.Priority = basePriority;

            OnStopConstruct?.Invoke();
        }
    }
}