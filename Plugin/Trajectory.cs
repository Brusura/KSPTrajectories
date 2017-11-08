﻿/*
Trajectories
Copyright 2014, Youen Toupin

This file is part of Trajectories, under MIT license.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KSP.Localization;
using UnityEngine;

namespace Trajectories
{
    /// <summary>
    /// Handles trajectory prediction, performing a lightweight physical simulation to
    /// predict a vessels trajectory in space and atmosphere.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    class Trajectory: MonoBehaviour
    {
        public class VesselState
        {
            public CelestialBody ReferenceBody { get; set; }

            // universal time
            public double Time { get; set; }

            // position in world frame relatively to the reference body
            public Vector3d Position { get; set; }

            // velocity in world frame relatively to the reference body
            public Vector3d Velocity { get; set; }

            // tells wether the patch starting from this state is superimposed on a stock KSP patch, or null if
            // something makes it diverge (atmospheric entry for example)
            public Orbit StockPatch { get; set; }

            public VesselState(Vessel vessel)
            {
                ReferenceBody = vessel.orbit.referenceBody;
                Time = Planetarium.GetUniversalTime();
                Position = vessel.GetWorldPos3D() - ReferenceBody.position;
                Velocity = vessel.obt_velocity;
                StockPatch = vessel.orbit;
            }

            public VesselState()
            {
            }
        }

        public struct Point
        {
            public Vector3 pos;
            public Vector3 aerodynamicForce;
            public Vector3 orbitalVelocity;

            /// <summary>
            /// Ground altitude above (or under) sea level, in meters.
            /// </summary>
            public float groundAltitude;

            /// <summary>
            /// Universal time
            /// </summary>
            public double time;
        }

        public class Patch
        {
            public VesselState StartingState { get; set; }

            public double EndTime { get; set; }

            public bool IsAtmospheric { get; set; }

            // // position array in body space (world frame centered on the body) ; only used when isAtmospheric is true
            public Point[] AtmosphericTrajectory { get; set; }

            // only used when isAtmospheric is false
            public Orbit SpaceOrbit { get; set; }

            public Vector3? ImpactPosition { get; set; }

            public Vector3? RawImpactPosition { get; set; }

            public Vector3? ImpactVelocity { get; set; }

            public Point GetInfo(float altitudeAboveSeaLevel)
            {
                if (!IsAtmospheric)
                    throw new Exception("Trajectory info available only for atmospheric patches");

                if (AtmosphericTrajectory.Length == 1)
                    return AtmosphericTrajectory[0];
                else if (AtmosphericTrajectory.Length == 0)
                    return new Point();

                float absAltitude = (float)StartingState.ReferenceBody.Radius + altitudeAboveSeaLevel;
                float sqMag = absAltitude * absAltitude;

                // TODO: optimize by doing a dichotomic search (this function assumes that altitude variation is monotonic anyway)
                int idx = 1;
                while (idx < AtmosphericTrajectory.Length && AtmosphericTrajectory[idx].pos.sqrMagnitude > sqMag)
                    ++idx;

                float coeff = (absAltitude - AtmosphericTrajectory[idx].pos.magnitude)
                    / Mathf.Max(0.00001f, AtmosphericTrajectory[idx - 1].pos.magnitude - AtmosphericTrajectory[idx].pos.magnitude);
                coeff = Math.Min(1.0f, Math.Max(0.0f, coeff));

                Point res = new Point
                {
                    pos = AtmosphericTrajectory[idx].pos * (1.0f - coeff) + AtmosphericTrajectory[idx - 1].pos * coeff,
                    aerodynamicForce = AtmosphericTrajectory[idx].aerodynamicForce * (1.0f - coeff) +
                                           AtmosphericTrajectory[idx - 1].aerodynamicForce * coeff,
                    orbitalVelocity = AtmosphericTrajectory[idx].orbitalVelocity * (1.0f - coeff) +
                                          AtmosphericTrajectory[idx - 1].orbitalVelocity * coeff,
                    groundAltitude = AtmosphericTrajectory[idx].groundAltitude * (1.0f - coeff) +
                                         AtmosphericTrajectory[idx - 1].groundAltitude * coeff,
                    time = AtmosphericTrajectory[idx].time * (1.0f - coeff) + AtmosphericTrajectory[idx - 1].time * coeff
                };

                return res;
            }
        }

        public static class Target
        {
            /// <summary>
            /// Targets reference body
            /// </summary>
            public static CelestialBody Body { get; set; } = null;

            /// <summary>
            /// Targets position in LocalSpace
            /// </summary>
            public static Vector3d? LocalPosition { get; set; } = null;

            /// <summary>
            /// Targets position in WorldSpace relative to the target body
            /// </summary>
            public static Vector3d? WorldPosition
            {
                get => LocalPosition.HasValue ? (Vector3d?)Body.transform.TransformDirection(LocalPosition.Value) : null;
                set
                {
                    LocalPosition = Body == null ? (Vector3d?)null : Body.transform.InverseTransformDirection((Vector3)value);
                }
            }

            /// <summary>
            /// Sets the target to a body and a position relative to that body.
            /// Passing a null or no arguments will clear the target.
            /// Also saves the target to the active vessel.
            /// </summary>
            public static void Set(CelestialBody body = null, Vector3d? position = null)
            {
                if (body != null && position.HasValue)
                {
                    Body = body;
                    WorldPosition = position;
                }
                else
                {
                    Body = null;
                    LocalPosition = null;
                }

                Save();
            }

            /// <summary>
            /// Saves the target to the active vessel module
            /// </summary>
            public static void Save()
            {
                if (FlightGlobals.ActiveVessel != null)
                {
                    foreach (var module in FlightGlobals.ActiveVessel.Parts.SelectMany(p => p.Modules.OfType<TrajectoriesVesselSettings>()))
                    {
                        module.hasTarget = LocalPosition != null;
                        module.TargetLocalSpacePosition = LocalPosition ?? new Vector3d();
                        module.TargetBody = Body == null ? "" : Body.name;
                    }
                }
            }
        }

        private int MaxIncrementTime { get { return 2; } }

        private Vessel vessel_;

        private VesselAerodynamicModel aerodynamicModel_;

        public string AerodynamicModelName
        {
            get
            {
                return aerodynamicModel_ == null ? Localizer.Format("#autoLOC_Trajectories_NotLoaded") :
                    aerodynamicModel_.AerodynamicModelName;
            }
        }

        private List<Patch> patches_ = new List<Patch>();

        private List<Patch> patchesBackBuffer_ = new List<Patch>();

        public List<Patch> Patches { get { return patches_; } }

        private Stopwatch incrementTime_;

        private IEnumerator<bool> partialComputation_;

        private float maxAccel_;

        private float maxAccelBackBuffer_;

        public float MaxAccel { get { return maxAccel_; } }

        private static int errorCount_;

        public int ErrorCount { get { return errorCount_; } }

        private static float frameTime_;

        private static float computationTime_;

        public float ComputationTime { get { return computationTime_ * 0.001f; } }

        private static Stopwatch gameFrameTime_;

        private static float averageGameFrameTime_;

        public float GameFrameTime { get { return averageGameFrameTime_ * 0.001f; } }

        private VesselState AddPatch_outState;

        // permit global access
        public static Trajectory fetch { get; private set; } = null;

        //  constructor
        public Trajectory()
        {
            fetch = this;
        }

        private void OnDestroy()
        {
            fetch = null;
        }

#if DEBUG && DEBUG_TELEMETRY

        // Awake is called only once when the script instance is being loaded. Used in place of the constructor for initialization.
        public void Awake()
        {
            // Add telemetry channels for real and predicted variable values
            Telemetry.AddChannel<double>("ut");
            Telemetry.AddChannel<double>("altitude");
            Telemetry.AddChannel<double>("airspeed");
            Telemetry.AddChannel<double>("aoa");
            Telemetry.AddChannel<float>("drag");

            Telemetry.AddChannel<double>("density");
            Telemetry.AddChannel<double>("density_calc");
            Telemetry.AddChannel<double>("density_calc_precise");

            Telemetry.AddChannel<double>("temperature");
            Telemetry.AddChannel<double>("temperature_calc");

            Telemetry.AddChannel<double>("force_actual");
            //Telemetry.AddChannel<double>("force_total");
            Telemetry.AddChannel<double>("force_predicted");
            Telemetry.AddChannel<double>("force_predicted_cache");
            //Telemetry.AddChannel<double>("force_reference");
        }

        public void FixedUpdate()
        {
            DebugTelemetry();
        }

        private static Vector3d PreviousFramePos;
        private static Vector3d PreviousFrameVelocity;
        private static double PreviousFrameTime = 0;


        public void DebugTelemetry()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                return;

            double now = Planetarium.GetUniversalTime();
            double dt = now - PreviousFrameTime;

            if (dt > 0.5 || dt < 0.0)
            {
                Vector3d bodySpacePosition = new Vector3d();
                Vector3d bodySpaceVelocity = new Vector3d();

                if (aerodynamicModel_ != null && vessel_ != null)
                {
                    CelestialBody body = vessel_.orbit.referenceBody;

                    bodySpacePosition = vessel_.GetWorldPos3D() - body.position;
                    bodySpaceVelocity = vessel_.obt_velocity;

                    double altitudeAboveSea = bodySpacePosition.magnitude - body.Radius;

                    Vector3d airVelocity = bodySpaceVelocity - body.getRFrmVel(body.position + bodySpacePosition);

                    double R = PreviousFramePos.magnitude;
                    Vector3d gravityForce = PreviousFramePos * (-body.gravParameter / (R * R * R) * vessel_.totalMass);

                    Quaternion inverseRotationFix = body.inverseRotation ?
                        Quaternion.AngleAxis((float)(body.angularVelocity.magnitude / Math.PI * 180.0 * dt), Vector3.up)
                        : Quaternion.identity;
                    Vector3d TotalForce = (bodySpaceVelocity - inverseRotationFix * PreviousFrameVelocity) * (vessel_.totalMass / dt);
                    TotalForce += bodySpaceVelocity * (dt * 0.000015); // numeric precision fix
                    Vector3d ActualForce = TotalForce - gravityForce;

                    Transform vesselTransform = vessel_.ReferenceTransform;
                    Vector3d vesselBackward = (Vector3d)(-vesselTransform.up.normalized);
                    Vector3d vesselForward = -vesselBackward;
                    Vector3d vesselUp = (Vector3d)(-vesselTransform.forward.normalized);
                    Vector3d vesselRight = Vector3d.Cross(vesselUp, vesselBackward).normalized;
                    double AoA = Math.Acos(Vector3d.Dot(airVelocity.normalized, vesselForward.normalized));
                    if (Vector3d.Dot(airVelocity, vesselUp) > 0)
                        AoA = -AoA;

                    VesselAerodynamicModel.DebugParts = true;
                    Vector3d referenceForce = aerodynamicModel_.ComputeForces(20000, new Vector3d(0, 0, 1500), new Vector3d(0,1,0), 0);
                    VesselAerodynamicModel.DebugParts = false;

                    Vector3d predictedForce = aerodynamicModel_.ComputeForces(altitudeAboveSea, airVelocity, vesselUp, AoA);
                    //VesselAerodynamicModel.Verbose = true;
                    Vector3d predictedForceWithCache = aerodynamicModel_.GetForces(body, bodySpacePosition, airVelocity, AoA);
                    //VesselAerodynamicModel.Verbose = false;

                    Vector3d localTotalForce = new Vector3d(
                        Vector3d.Dot(TotalForce, vesselRight),
                        Vector3d.Dot(TotalForce, vesselUp),
                        Vector3d.Dot(TotalForce, vesselBackward));
                    Vector3d localActualForce = new Vector3d(
                        Vector3d.Dot(ActualForce, vesselRight),
                        Vector3d.Dot(ActualForce, vesselUp),
                        Vector3d.Dot(ActualForce, vesselBackward));
                    Vector3d localPredictedForce = new Vector3d(
                        Vector3d.Dot(predictedForce, vesselRight),
                        Vector3d.Dot(predictedForce, vesselUp),
                        Vector3d.Dot(predictedForce, vesselBackward));
                    Vector3d localPredictedForceWithCache = new Vector3d(
                        Vector3d.Dot(predictedForceWithCache, vesselRight),
                        Vector3d.Dot(predictedForceWithCache, vesselUp),
                        Vector3d.Dot(predictedForceWithCache, vesselBackward));

                    Telemetry.Send("ut", now);
                    Telemetry.Send("altitude", vessel_.altitude);

                    Telemetry.Send("airspeed", Math.Floor(airVelocity.magnitude));
                    Telemetry.Send("aoa", (AoA * 180.0 / Math.PI));

                    Telemetry.Send("force_actual", localActualForce.magnitude);
                    //Telemetry.Send("force_actual.x", localActualForce.x);
                    //Telemetry.Send("force_actual.y", localActualForce.y);
                    //Telemetry.Send("force_actual.z", localActualForce.z);


                    //Telemetry.Send("force_total", localTotalForce.magnitude);
                    //Telemetry.Send("force_total.x", localTotalForce.x);
                    //Telemetry.Send("force_total.y", localTotalForce.y);
                    //Telemetry.Send("force_total.z", localTotalForce.z);

                    Telemetry.Send("force_predicted", localPredictedForce.magnitude);
                    //Telemetry.Send("force_predicted.x", localPredictedForce.x);
                    //Telemetry.Send("force_predicted.y", localPredictedForce.y);
                    //Telemetry.Send("force_predicted.z", localPredictedForce.z);

                    Telemetry.Send("force_predicted_cache", localPredictedForceWithCache.magnitude);
                    //Telemetry.Send("force_predicted_cache.x", localPredictedForceWithCache.x);
                    //Telemetry.Send("force_predicted_cache.y", localPredictedForceWithCache.y);
                    //Telemetry.Send("force_predicted_cache.z", localPredictedForceWithCache.z);

                    //Telemetry.Send("force_reference", referenceForce.magnitude);
                    //Telemetry.Send("force_reference.x", referenceForce.x);
                    //Telemetry.Send("force_reference.y", referenceForce.y);
                    //Telemetry.Send("force_reference.z", referenceForce.z);

                    //Telemetry.Send("velocity.x", bodySpaceVelocity.x);
                    //Telemetry.Send("velocity.y", bodySpaceVelocity.y);
                    //Telemetry.Send("velocity.z", bodySpaceVelocity.z);

                    //Vector3d velocity_pos = (bodySpacePosition - PreviousFramePos) / dt;
                    //Telemetry.Send("velocity_pos.x", velocity_pos.x);
                    //Telemetry.Send("velocity_pos.y", velocity_pos.y);
                    //Telemetry.Send("velocity_pos.z", velocity_pos.z);

                    Telemetry.Send("drag", vessel_.rootPart.rb.drag);

                    Telemetry.Send("density", vessel_.atmDensity);
                    Telemetry.Send("density_calc", StockAeroUtil.GetDensity(altitudeAboveSea, body));
                    Telemetry.Send("density_calc_precise", StockAeroUtil.GetDensity(vessel_.GetWorldPos3D(), body));

                    Telemetry.Send("temperature", vessel_.atmosphericTemperature);
                    Telemetry.Send("temperature_calc", StockAeroUtil.GetTemperature(vessel_.GetWorldPos3D(), body));
                }

                PreviousFrameVelocity = bodySpaceVelocity;
                PreviousFramePos = bodySpacePosition;
                PreviousFrameTime = now;
            }
        }
#endif

        public void Update()
        {
            // compute frame time
            computationTime_ = computationTime_ * 0.99f + frameTime_ * 0.01f;
            float offset = frameTime_ - computationTime_;
            frameTime_ = 0;

            if (gameFrameTime_ != null)
            {
                float t = (float)gameFrameTime_.ElapsedMilliseconds;
                averageGameFrameTime_ = averageGameFrameTime_ * 0.99f + t * 0.01f;
            }
            gameFrameTime_ = Stopwatch.StartNew();

            // is current trajectory vessel the active vessel?
            if (Util.IsFlight && (vessel_ != FlightGlobals.ActiveVessel))
            {
                // load target data from vessel if module exists
                TrajectoriesVesselSettings module = null;
                if (FlightGlobals.ActiveVessel != null)
                {
                    module = FlightGlobals.ActiveVessel.Parts.SelectMany(p => p.Modules.OfType<TrajectoriesVesselSettings>())
                        .FirstOrDefault();
                }

                CelestialBody body = null;
                if (module != null)
                    body = FlightGlobals.Bodies.FirstOrDefault(b => b.name == module.TargetBody);

                if (body == null || !module.hasTarget)
                {
                    // clear target and save to vessel module
                    Target.Set();
                }
                else
                {
                    // set target data from vessel module
                    Target.Body = body;
                    Target.LocalPosition = module.TargetLocalSpacePosition;
                }
            }

            // should the trajectory be calculated?
            if (Util.IsFlight
                && FlightGlobals.ActiveVessel != null
                && FlightGlobals.ActiveVessel.Parts.Count != 0
                && ((Settings.fetch.DisplayTrajectories)
                    || Settings.fetch.AlwaysUpdate
                    || Target.LocalPosition.HasValue))
            {
                ComputeTrajectory(FlightGlobals.ActiveVessel, DescentProfile.fetch);
            }
        }

        public void InvalidateAerodynamicModel()
        {
            aerodynamicModel_.Invalidate();
        }

        public void ComputeTrajectory(Vessel vessel, float AoA)
        {
            DescentProfile profile = new DescentProfile(AoA);
            ComputeTrajectory(vessel, profile);
        }

        public void ComputeTrajectory(Vessel vessel, DescentProfile profile)
        {
            try
            {
                incrementTime_ = Stopwatch.StartNew();

                if (partialComputation_ == null || vessel != vessel_)
                {
                    patchesBackBuffer_.Clear();
                    maxAccelBackBuffer_ = 0;

                    vessel_ = vessel;

                    if (vessel == null)
                    {
                        patches_.Clear();
                        return;
                    }

                    if (partialComputation_ != null)
                        partialComputation_.Dispose();
                    partialComputation_ = ComputeTrajectoryIncrement(vessel, profile).GetEnumerator();
                }

                bool finished = !partialComputation_.MoveNext();

                if (finished)
                {
                    var tmp = patches_;
                    patches_ = patchesBackBuffer_;
                    patchesBackBuffer_ = tmp;

                    maxAccel_ = maxAccelBackBuffer_;

                    partialComputation_.Dispose();
                    partialComputation_ = null;
                }

                frameTime_ += (float)incrementTime_.ElapsedMilliseconds;
            }
            catch (Exception)
            {
                ++errorCount_;
                throw;
            }
        }

        private IEnumerable<bool> ComputeTrajectoryIncrement(Vessel vessel, DescentProfile profile)
        {
            if (aerodynamicModel_ == null || !aerodynamicModel_.isValidFor(vessel, vessel.mainBody))
                aerodynamicModel_ = AerodynamicModelFactory.GetModel(vessel, vessel.mainBody);
            else
                aerodynamicModel_.IncrementalUpdate();

            var state = vessel.LandedOrSplashed ? null : new VesselState(vessel);
            for (int patchIdx = 0; patchIdx < Settings.fetch.MaxPatchCount; ++patchIdx)
            {
                if (state == null)
                    break;

                if (incrementTime_.ElapsedMilliseconds > MaxIncrementTime)
                    yield return false;

                if (null != vessel_.patchedConicSolver)
                {
                    var maneuverNodes = vessel_.patchedConicSolver.maneuverNodes;
                    foreach (var node in maneuverNodes)
                    {
                        if (node.UT == state.Time)
                        {
                            state.Velocity += node.GetBurnVector(CreateOrbitFromState(state));
                            break;
                        }
                    }
                    foreach (var result in AddPatch(state, profile))
                        yield return false;
                }

                state = AddPatch_outState;
            }
        }

        /// <summary>
        /// relativePosition is in world frame, but relative to the body (i.e. inertial body space)
        /// returns the altitude above sea level (can be negative for bodies without ocean)
        /// </summary>
        public static double GetGroundAltitude(CelestialBody body, Vector3 relativePosition)
        {
            if (body.pqsController == null)
                return 0;

            double lat = body.GetLatitude(relativePosition + body.position) / 180.0 * Math.PI;
            double lon = body.GetLongitude(relativePosition + body.position) / 180.0 * Math.PI;
            Vector3d rad = new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), Math.Cos(lat) * Math.Sin(lon));
            double elevation = body.pqsController.GetSurfaceHeight(rad) - body.Radius;
            if (body.ocean)
                elevation = Math.Max(elevation, 0.0);

            return elevation;
        }

        public static double RealMaxAtmosphereAltitude(CelestialBody body)
        {
            if (!body.atmosphere)
                return 0;
            // Change for 1.0 refer to atmosphereDepth
            return body.atmosphereDepth;
        }

        private Orbit CreateOrbitFromState(VesselState state)
        {
            var orbit = new Orbit();
            orbit.UpdateFromStateVectors(Util.SwapYZ(state.Position), Util.SwapYZ(state.Velocity), state.ReferenceBody, state.Time);
            var pars = new PatchedConics.SolverParameters
            {
                FollowManeuvers = false
            };
            PatchedConics.CalculatePatch(orbit, new Orbit(), state.Time, pars, null);
            return orbit;
        }

        private double FindOrbitBodyIntersection(Orbit orbit, double startTime, double endTime, double bodyAltitude)
        {
            // binary search of entry time in atmosphere
            // I guess an analytic solution could be found, but I'm too lazy to search it

            double from = startTime;
            double to = endTime;

            int loopCount = 0;
            while (to - from > 0.1)
            {
                ++loopCount;
                if (loopCount > 1000)
                {
                    UnityEngine.Debug.Log("WARNING: infinite loop? (Trajectories.Trajectory.AddPatch, atmosphere limit search)");
                    ++errorCount_;
                    break;
                }
                double middle = (from + to) * 0.5;
                if (orbit.getRelativePositionAtUT(middle).magnitude < bodyAltitude)
                {
                    to = middle;
                }
                else
                {
                    from = middle;
                }
            }

            return to;
        }

        private IEnumerable<bool> AddPatch(VesselState startingState, DescentProfile profile)
        {
            if (null == vessel_.patchedConicSolver)
            {
                UnityEngine.Debug.LogWarning("Trajectories: AddPatch() attempted when patchedConicsSolver is null; Skipping.");
                yield break;
            }

            CelestialBody body = startingState.ReferenceBody;

            var patch = new Patch
            {
                StartingState = startingState,
                IsAtmospheric = false,
                SpaceOrbit = startingState.StockPatch ?? CreateOrbitFromState(startingState)
            };
            patch.EndTime = patch.StartingState.Time + patch.SpaceOrbit.period;

            // the flight plan does not always contain the first patches (before the first maneuver node),
            // so we populate it with the current orbit and associated encounters etc.
            var flightPlan = new List<Orbit>();
            for (var orbit = vessel_.orbit; orbit != null && orbit.activePatch; orbit = orbit.nextPatch)
            {
                if (vessel_.patchedConicSolver.flightPlan.Contains(orbit))
                    break;
                flightPlan.Add(orbit);
            }

            foreach (var orbit in vessel_.patchedConicSolver.flightPlan)
            {
                flightPlan.Add(orbit);
            }


            Orbit nextPatch = null;
            if (startingState.StockPatch == null)
            {
                nextPatch = patch.SpaceOrbit.nextPatch;
            }
            else
            {
                int planIdx = flightPlan.IndexOf(startingState.StockPatch);
                if (planIdx >= 0 && planIdx < flightPlan.Count - 1)
                {
                    nextPatch = flightPlan[planIdx + 1];
                }
            }

            if (nextPatch != null)
            {
                patch.EndTime = nextPatch.StartUT;
            }

            double maxAtmosphereAltitude = RealMaxAtmosphereAltitude(body);
            if (!body.atmosphere)
            {
                maxAtmosphereAltitude = body.pqsController.mapMaxHeight;
            }

            double minAltitude = patch.SpaceOrbit.PeA;
            if (patch.SpaceOrbit.timeToPe < 0 || patch.EndTime < startingState.Time + patch.SpaceOrbit.timeToPe)
            {
                minAltitude = Math.Min(
                    patch.SpaceOrbit.getRelativePositionAtUT(patch.EndTime).magnitude,
                    patch.SpaceOrbit.getRelativePositionAtUT(patch.StartingState.Time + 1.0).magnitude
                    ) - body.Radius;
            }
            if (minAltitude < maxAtmosphereAltitude)
            {
                double entryTime;
                if (startingState.Position.magnitude <= body.Radius + maxAtmosphereAltitude)
                {
                    // whole orbit is inside the atmosphere
                    entryTime = startingState.Time;
                }
                else
                {
                    entryTime = FindOrbitBodyIntersection(
                        patch.SpaceOrbit,
                        startingState.Time, startingState.Time + patch.SpaceOrbit.timeToPe,
                        body.Radius + maxAtmosphereAltitude);
                }

                if (entryTime > startingState.Time + 0.1 || !body.atmosphere)
                {
                    if (body.atmosphere)
                    {
                        // add the space patch before atmospheric entry

                        patch.EndTime = entryTime;
                        patchesBackBuffer_.Add(patch);
                        AddPatch_outState = new VesselState
                        {
                            Position = Util.SwapYZ(patch.SpaceOrbit.getRelativePositionAtUT(entryTime)),
                            ReferenceBody = body,
                            Time = entryTime,
                            Velocity = Util.SwapYZ(patch.SpaceOrbit.getOrbitalVelocityAtUT(entryTime))
                        };
                        yield break;
                    }
                    else
                    {
                        // the body has no atmosphere, so what we actually computed is the entry
                        // inside the "ground sphere" (defined by the maximal ground altitude)
                        // now we iterate until the inner ground sphere (minimal altitude), and
                        // check if we hit the ground along the way
                        double groundRangeExit = FindOrbitBodyIntersection(
                            patch.SpaceOrbit,
                            startingState.Time, startingState.Time + patch.SpaceOrbit.timeToPe,
                            body.Radius - maxAtmosphereAltitude);

                        if (groundRangeExit <= entryTime)
                            groundRangeExit = startingState.Time + patch.SpaceOrbit.timeToPe;

                        double iterationSize = (groundRangeExit - entryTime) / 100.0;
                        double t;
                        bool groundImpact = false;
                        for (t = entryTime; t < groundRangeExit; t += iterationSize)
                        {
                            Vector3d pos = patch.SpaceOrbit.getRelativePositionAtUT(t);
                            double groundAltitude = GetGroundAltitude(body, CalculateRotatedPosition(body, Util.SwapYZ(pos), t))
                                + body.Radius;
                            if (pos.magnitude < groundAltitude)
                            {
                                t -= iterationSize;
                                groundImpact = true;
                                break;
                            }
                        }

                        if (groundImpact)
                        {
                            patch.EndTime = t;
                            patch.RawImpactPosition = Util.SwapYZ(patch.SpaceOrbit.getRelativePositionAtUT(t));
                            patch.ImpactPosition = CalculateRotatedPosition(body, patch.RawImpactPosition.Value, t);
                            patch.ImpactVelocity = Util.SwapYZ(patch.SpaceOrbit.getOrbitalVelocityAtUT(t));
                            patchesBackBuffer_.Add(patch);
                            AddPatch_outState = null;
                            yield break;
                        }
                        else
                        {
                            // no impact, just add the space orbit
                            patchesBackBuffer_.Add(patch);
                            if (nextPatch != null)
                            {
                                AddPatch_outState = new VesselState
                                {
                                    Position = Util.SwapYZ(patch.SpaceOrbit.getRelativePositionAtUT(patch.EndTime)),
                                    Velocity = Util.SwapYZ(patch.SpaceOrbit.getOrbitalVelocityAtUT(patch.EndTime)),
                                    ReferenceBody = nextPatch == null ? body : nextPatch.referenceBody,
                                    Time = patch.EndTime,
                                    StockPatch = nextPatch
                                };
                                yield break;
                            }
                            else
                            {
                                AddPatch_outState = null;
                                yield break;
                            }
                        }
                    }
                }
                else
                {
                    if (patch.StartingState.ReferenceBody != vessel_.mainBody)
                    {
                        // currently, we can't handle predictions for another body, so we stop
                        AddPatch_outState = null;
                        yield break;
                    }

                    // simulate atmospheric flight (drag and lift), until impact or atmosphere exit
                    // (typically for an aerobraking maneuver) assuming a constant angle of attack
                    patch.IsAtmospheric = true;
                    patch.StartingState.StockPatch = null;

                    // lower dt would be more accurate, but a tradeoff has to be found between performances and accuracy
                    double dt = 0.1;

                    // some shallow entries can result in very long flight. For performances reasons,
                    // we limit the prediction duration
                    int maxIterations = (int)(30.0 * 60.0 / dt);

                    int chunkSize = 128;

                    // time between two consecutive stored positions (more intermediate positions are computed for better accuracy),
                    // also used for ground collision checks
                    double trajectoryInterval = 10.0;

                    var buffer = new List<Point[]>
                    {
                        new Point[chunkSize]
                    };
                    int nextPosIdx = 0;

                    Vector3d pos = Util.SwapYZ(patch.SpaceOrbit.getRelativePositionAtUT(entryTime));
                    Vector3d vel = Util.SwapYZ(patch.SpaceOrbit.getOrbitalVelocityAtUT(entryTime));

                    //Util.PostSingleScreenMessage("atmo start cond", "Atmospheric start: vel=" + vel.ToString("0.00") + " (mag=" + vel.magnitude.ToString("0.00") + ")");

                    Vector3d prevPos = pos - vel * dt;
                    double currentTime = entryTime;
                    double lastPositionStoredUT = 0;
                    Vector3d lastPositionStored = new Vector3d();
                    bool hitGround = false;
                    int iteration = 0;
                    int incrementIterations = 0;
                    int minIterationsPerIncrement = maxIterations / Settings.fetch.MaxFramesPerPatch;
                    double accumulatedForces = 0;
                    while (true)
                    {
                        ++iteration;
                        ++incrementIterations;

                        if (incrementIterations > minIterationsPerIncrement && incrementTime_.ElapsedMilliseconds > MaxIncrementTime)
                        {
                            yield return false;
                            incrementIterations = 0;
                        }

                        double R = pos.magnitude;
                        double altitude = R - body.Radius;
                        double atmosphereCoeff = altitude / maxAtmosphereAltitude;
                        if (hitGround
                            || atmosphereCoeff <= 0.0 || atmosphereCoeff >= 1.0
                            || iteration == maxIterations || currentTime > patch.EndTime)
                        {
                            //Util.PostSingleScreenMessage("atmo force", "Atmospheric accumulated force: " + accumulatedForces.ToString("0.00"));

                            if (hitGround || atmosphereCoeff <= 0.0)
                            {
                                patch.RawImpactPosition = pos;
                                patch.ImpactPosition = CalculateRotatedPosition(body, patch.RawImpactPosition.Value, currentTime);
                                patch.ImpactVelocity = vel;
                            }

                            patch.EndTime = Math.Min(currentTime, patch.EndTime);

                            int totalCount = (buffer.Count - 1) * chunkSize + nextPosIdx;
                            patch.AtmosphericTrajectory = new Point[totalCount];
                            int outIdx = 0;
                            foreach (var chunk in buffer)
                            {
                                foreach (var p in chunk)
                                {
                                    if (outIdx == totalCount)
                                        break;
                                    patch.AtmosphericTrajectory[outIdx++] = p;
                                }
                            }

                            if (iteration == maxIterations)
                            {
                                ScreenMessages.PostScreenMessage("WARNING: trajectory prediction stopped, too many iterations");
                                patchesBackBuffer_.Add(patch);
                                AddPatch_outState = null;
                                yield break;
                            }
                            else if (atmosphereCoeff <= 0.0 || hitGround)
                            {
                                patchesBackBuffer_.Add(patch);
                                AddPatch_outState = null;
                                yield break;
                            }
                            else
                            {
                                patchesBackBuffer_.Add(patch);
                                AddPatch_outState = new VesselState
                                {
                                    Position = pos,
                                    Velocity = vel,
                                    ReferenceBody = body,
                                    Time = patch.EndTime
                                };
                                yield break;
                            }
                        }

                        Vector3d gravityAccel = pos * (-body.gravParameter / (R * R * R));

                        //Util.PostSingleScreenMessage("prediction vel", "prediction vel = " + vel);
                        Vector3d airVelocity = vel - body.getRFrmVel(body.position + pos);
                        double angleOfAttack = profile.GetAngleOfAttack(body, pos, airVelocity);
                        Vector3d aerodynamicForce = aerodynamicModel_.GetForces(body, pos, airVelocity, angleOfAttack);
                        accumulatedForces += aerodynamicForce.magnitude * dt;
                        Vector3d acceleration = gravityAccel + aerodynamicForce / aerodynamicModel_.mass;

                        // acceleration in the vessel reference frame is acceleration - gravityAccel
                        maxAccelBackBuffer_ = Math.Max(
                            (float)(aerodynamicForce.magnitude / aerodynamicModel_.mass),
                            maxAccelBackBuffer_);

                        //vel += acceleration * dt;
                        //pos += vel * dt;

                        // Verlet integration (more precise than using the velocity)
                        Vector3d ppos = prevPos;
                        prevPos = pos;
                        pos = pos + pos - ppos + acceleration * (dt * dt);
                        vel = (pos - prevPos) / dt;

                        currentTime += dt;

                        double interval = altitude < 10000.0 ? trajectoryInterval * 0.1 : trajectoryInterval;
                        if (currentTime >= lastPositionStoredUT + interval)
                        {
                            double groundAltitude = GetGroundAltitude(body, CalculateRotatedPosition(body, pos, currentTime));
                            if (lastPositionStoredUT > 0)
                            {
                                // check terrain collision, to detect impact on mountains etc.
                                Vector3 rayOrigin = lastPositionStored;
                                Vector3 rayEnd = pos;
                                double absGroundAltitude = groundAltitude + body.Radius;
                                if (absGroundAltitude > rayEnd.magnitude)
                                {
                                    hitGround = true;
                                    float coeff = Math.Max(0.01f, (float)((absGroundAltitude - rayOrigin.magnitude)
                                        / (rayEnd.magnitude - rayOrigin.magnitude)));
                                    pos = rayEnd * coeff + rayOrigin * (1.0f - coeff);
                                    currentTime = currentTime * coeff + lastPositionStoredUT * (1.0f - coeff);
                                }
                            }

                            lastPositionStoredUT = currentTime;
                            if (nextPosIdx == chunkSize)
                            {
                                buffer.Add(new Point[chunkSize]);
                                nextPosIdx = 0;
                            }
                            Vector3d nextPos = pos;
                            if (Settings.fetch.BodyFixedMode)
                            {
                                nextPos = CalculateRotatedPosition(body, nextPos, currentTime);
                            }
                            buffer.Last()[nextPosIdx].aerodynamicForce = aerodynamicForce;
                            buffer.Last()[nextPosIdx].orbitalVelocity = vel;
                            buffer.Last()[nextPosIdx].groundAltitude = (float)groundAltitude;
                            buffer.Last()[nextPosIdx].time = currentTime;
                            buffer.Last()[nextPosIdx++].pos = nextPos;
                            lastPositionStored = pos;
                        }
                    }
                }
            }
            else
            {
                // no atmospheric entry, just add the space orbit
                patchesBackBuffer_.Add(patch);
                if (nextPatch != null)
                {
                    AddPatch_outState = new VesselState
                    {
                        Position = Util.SwapYZ(patch.SpaceOrbit.getRelativePositionAtUT(patch.EndTime)),
                        Velocity = Util.SwapYZ(patch.SpaceOrbit.getOrbitalVelocityAtUT(patch.EndTime)),
                        ReferenceBody = nextPatch == null ? body : nextPatch.referenceBody,
                        Time = patch.EndTime,
                        StockPatch = nextPatch
                    };
                    yield break;
                }
                else
                {
                    AddPatch_outState = null;
                    yield break;
                }
            }
        }

        public static Vector3 CalculateRotatedPosition(CelestialBody body, Vector3 relativePosition, double time)
        {
            float angle = (float)(-(time - Planetarium.GetUniversalTime()) * body.angularVelocity.magnitude / Math.PI * 180.0);
            Quaternion bodyRotation = Quaternion.AngleAxis(angle, body.angularVelocity.normalized);
            return bodyRotation * relativePosition;
        }

        public static Vector3d GetWorldPositionAtUT(Orbit orbit, double ut)
        {
            Vector3d worldPos = Util.SwapYZ(orbit.getRelativePositionAtUT(ut));
            if (orbit.referenceBody != FlightGlobals.Bodies[0])
                worldPos += GetWorldPositionAtUT(orbit.referenceBody.orbit, ut);
            return worldPos;
        }

    }
}
