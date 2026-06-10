// ============================================================================
// VERBATIM ENGINE EXCERPT — the per-tick ship physics integration.
// Original: CshipIGC::ExecuteShipMove(). Reproduced here so the homage project
// does not need the original repository. Comments are from the original source.
// ============================================================================
//
// Inputs:
//   timeStart, timeStop : tick window (seconds-valued Time)
//   pVelocity           : in/out world velocity vector
//   pOrientation        : in/out orientation matrix (ship attitude)
// Member state used:
//   m_controls.jsValues[c_axisYaw|Pitch|Roll|Throttle]  : stick, each in [-1,1]
//   m_turnRates[3]      : persistent current angular velocity per axis (rad/s)
//   m_stateM            : bitmask of pressed buttons (see ShipControlStateIGC)
//   m_engineVector      : scratch world-space thrust vector
//   m_pmodelRipcord     : non-null while warping (disables maneuvering)
// Hull getters (all already multiplied by per-team Global Attributes, default 1.0):
//   GetThrust(), GetMass(), GetMaxSpeed(), GetMaxTurnRate(axis),
//   GetTurnTorque(axis), GetSideMultiplier(), GetBackMultiplier()

void    CshipIGC::ExecuteShipMove(Time          timeStart,
                                  Time          timeStop,
                                  Vector*       pVelocity,
                                  Orientation*  pOrientation)
{
    if (timeStop > timeStart)
    {
        //Adjust ship's heading, velocity, etc. based on its control settings.
        float   dT = timeStop - timeStart;

        float   thrust = m_myHullType.GetThrust();
        float   thrust2 = thrust * thrust;

        //Conversion factor ... Newtons to deltaV
        float   thrustToVelocity = dT / GetMass();

        //No maneuvering if ripcording
        if (!m_pmodelRipcord)
        {
            //constrain the desired yaw/pitch/roll rates to a sphere rather than a box
            float   l = m_controls.jsValues[c_axisYaw]   * m_controls.jsValues[c_axisYaw] +
                        m_controls.jsValues[c_axisPitch] * m_controls.jsValues[c_axisPitch] +
                        m_controls.jsValues[c_axisRoll]  * m_controls.jsValues[c_axisRoll];

            if (l > 1.0f)
                l = 1.0f / sqrt(l);
            else
                l = 1.0f;

            float   tm = GetTorqueMultiplier() * thrustToVelocity;
            for (int i = 0; (i < 3); i++)
            {
                float   desiredRate = m_controls.jsValues[i] * l * m_myHullType.GetMaxTurnRate(i);
                float   maxDelta    = tm * m_myHullType.GetTurnTorque(i);

                if (desiredRate < m_turnRates[i] - maxDelta)
                    m_turnRates[i] -= maxDelta;
                else if (desiredRate > m_turnRates[i] + maxDelta)
                    m_turnRates[i] += maxDelta;
                else
                    m_turnRates[i] = desiredRate;
            }
        }

        pOrientation->Yaw(   m_turnRates[c_axisYaw]   * dT);
        pOrientation->Pitch(-m_turnRates[c_axisPitch] * dT);   // note: pitch negated
        pOrientation->Roll(  m_turnRates[c_axisRoll]  * dT);

        // Re-normalize the orientation matrix
        pOrientation->Renormalize();

        const Vector&   myBackward = pOrientation->GetBackward();

        float   speed = pVelocity->Length();
        float   maxSpeed = m_myHullType.GetMaxSpeed();

        //What would our velocity be if we simply let drag slow us down
        Vector  drag;
        {
            double   f = exp(double(double(-thrust) * double(thrustToVelocity) / (double)maxSpeed));

            //New velocity = old velocity * f
            //drag = thrust required to create this change in velocity
            drag = *pVelocity * float((1.0 - f) / double(thrustToVelocity));
        }

        m_engineVector.x = m_engineVector.y = m_engineVector.z = 0.0f;    //Zero out the thrust

        bool    afterF = (m_stateM & afterburnerButtonIGC) != 0;
        float   thrustRatio = 0.0f;
        {
            IafterburnerIGC*    afterburner = (IafterburnerIGC*)(m_mountedOthers[ET_Afterburner]);

            if (afterburner)
            {
                float   abThrust = afterburner->GetMaxThrustWithGA();
                if (afterF) {
                    thrustRatio = abThrust / thrust;
                }
                afterburner->IncrementalUpdate(timeStart, timeStop, false);

                float power = afterburner->GetPower();   // ramps 0..1 via onRate/offRate
                if (power != 0.0f)
                {
                    //Factor the afterburner thrust into drag (so it factors into engine thrust)
                    drag += (power * abThrust) * myBackward;
                }
            }
        }

        //no maneuvering while ripcording
        if (!m_pmodelRipcord)
        {
            Vector  localThrust;
            if (m_stateM & (leftButtonIGC | rightButtonIGC |
                            upButtonIGC | downButtonIGC |
                            forwardButtonIGC | backwardButtonIGC))
            {
                //Under manual control: find out which direction to thrust in
                int   x = ((m_stateM & leftButtonIGC)     ? -1 : 0) + ((m_stateM & rightButtonIGC)   ?  1 : 0);
                int   y = ((m_stateM & downButtonIGC)     ? -1 : 0) + ((m_stateM & upButtonIGC)      ?  1 : 0);
                int   z = ((m_stateM & backwardButtonIGC) ?  1 : 0) + ((m_stateM & forwardButtonIGC) ? -1 : 0);

                if (x || y || z)
                {
                    localThrust.x = (thrust * (float)x);
                    localThrust.y = (thrust * (float)y);
                    localThrust.z = (thrust * (float)z);
                }
                else
                    localThrust = Vector::GetZero();
            }
            else
            {
                if ((m_stateM & coastButtonIGC) && !afterF)
                    localThrust = pOrientation->TimesInverse(drag);   // coast: exactly cancel drag
                else
                {
                    float   negDesiredSpeed;
                    if (afterF)
                        negDesiredSpeed = maxSpeed * (-1.0f - thrustRatio);
                    else
                        negDesiredSpeed = (-0.5f * (1.0f + m_controls.jsValues[c_axisThrottle])) *
                                          ((speed > maxSpeed) ? speed : maxSpeed);

                    Vector  desiredVelocity = myBackward * negDesiredSpeed;

                    //Thrust required to obtain desired velocity, accounting for drag
                    localThrust = pOrientation->TimesInverse((desiredVelocity - *pVelocity) / thrustToVelocity + drag);
                }
            }

            {
                //Clip the engine vector to the available thrust from the engine.
                //Strafe (x,y) divided by side mult; reverse (z>0) divided by back mult.
                float   sm = m_myHullType.GetSideMultiplier();
                Vector  scaledThrust(localThrust.x / sm,
                                     localThrust.y / sm,
                                     localThrust.z <= 0.0f ? localThrust.z
                                                           : (localThrust.z / m_myHullType.GetBackMultiplier()));

                float   r2 = scaledThrust.LengthSquared();

                if (r2 == 0.0f)
                    m_engineVector = Vector::GetZero();
                else if (r2 <= thrust2)
                    m_engineVector = localThrust * *pOrientation;                       // no clipping
                else
                    m_engineVector = (localThrust * *pOrientation) * (thrust / (float)sqrt(r2));  // clip
            }
        }

        *pVelocity += thrustToVelocity * (m_engineVector - drag);
    }
}

// ============================================================================
// Speed-dependent agility (original: CshipIGC::GetTorqueMultiplier()).
// Ranges 0.50 at rest -> 1.00 at/above max speed.
// ============================================================================
float CshipIGC::GetTorqueMultiplier(void) const
{
    static const float  c_fMultiplierAtZero = 0.50f;
    float   fraction = GetVelocity().Length() / m_myHullType.GetMaxSpeed();
    return  c_fMultiplierAtZero + (1.0f - c_fMultiplierAtZero) * 2.0f * fraction / (fraction + 1.0f);
}

// ============================================================================
// Hull stat getters (original: MyHullType::*). Each raw stored value is scaled
// by a per-team Global Attribute (tech upgrades). Defaults are all 1.0f.
// ============================================================================
float MyHullType::GetMaxSpeed(void)        const { return m_pHullData->speed            * GA(c_gaMaxSpeed);   }
float MyHullType::GetMaxTurnRate(Axis a)   const { return m_pHullData->maxTurnRates[a]  * GA(c_gaTurnRate);   }
float MyHullType::GetTurnTorque(Axis a)    const { return m_pHullData->turnTorques[a]   * GA(c_gaTurnTorque); }
float MyHullType::GetThrust(void)          const { return m_pHullData->thrust           * GA(c_gaThrust);     }
float MyHullType::GetSideMultiplier(void)  const { return m_pHullData->sideMultiplier;  }
float MyHullType::GetBackMultiplier(void)  const { return m_pHullData->backMultiplier;  }

// ============================================================================
// Afterburner power ramp (original: CafterburnerIGC::IncrementalUpdate(), core).
// onRate / offRate are per-afterburner-type stats; power is clamped to [0,1].
// ============================================================================
//   if activated:  m_power += dt * onRate;  clamp <= 1.0
//   else:          m_power -= dt * offRate; if <= 0 deactivate
//   fuelUsed = m_power * fuelConsumption * maxThrust * dt
