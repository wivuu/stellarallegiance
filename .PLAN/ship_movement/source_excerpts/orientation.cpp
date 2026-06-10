// ============================================================================
// VERBATIM ENGINE EXCERPT — orientation (attitude) math.
// Original: class Orientation (zlib/orientation.{h,cpp}).
//
// Orientation is a 3x3 rotation matrix m_r[row][col]. Rows are the ship's local
// axes expressed in world space:
//   row 0 = Right   (GetRight)
//   row 1 = Up      (GetUp)
//   row 2 = -Forward (so GetForward() negates row 2; GetBackward() returns row 2)
//
// A world vector v is rotated into world space from ship-local by  v * orientation
// (see operator* below). TimesInverse(v) maps world -> ship-local.
//
// Yaw/Pitch/Roll LEFT-multiply the matrix by an axis rotation, i.e. they rotate
// the ship about its own current local axis by `theta` radians.
// ============================================================================

class Orientation {
    float m_r[3][3];
public:
    Vector GetForward()  const { return Vector(-m_r[2][0], -m_r[2][1], -m_r[2][2]); }
    const Vector& GetBackward() const { return *((Vector*)&m_r[2][0]); } // row 2
    const Vector& GetUp()       const { return *((Vector*)&m_r[1][0]); } // row 1
    const Vector& GetRight()    const { return *((Vector*)&m_r[0][0]); } // row 0
    // ...
};

// v * orientation  : ship-local vector -> world (columns of m_r)
inline Vector operator*(const Vector& v, const Orientation& o) {
    return Vector(
        v.x*o.m_r[0][0] + v.y*o.m_r[1][0] + v.z*o.m_r[2][0],
        v.x*o.m_r[0][1] + v.y*o.m_r[1][1] + v.z*o.m_r[2][1],
        v.x*o.m_r[0][2] + v.y*o.m_r[1][2] + v.z*o.m_r[2][2]);
}

// world vector -> ship-local (multiply by transpose / inverse of rotation)
Vector Orientation::TimesInverse(const Vector& xyz) const {
    return Vector(
        xyz.x*m_r[0][0] + xyz.y*m_r[0][1] + xyz.z*m_r[0][2],
        xyz.x*m_r[1][0] + xyz.y*m_r[1][1] + xyz.z*m_r[1][2],
        xyz.x*m_r[2][0] + xyz.y*m_r[2][1] + xyz.z*m_r[2][2]);
}

// Rotate about local PITCH axis (X) by theta.
//        [ 1      0     0]
// this = [ 0  cos t sin t] * this
//        [ 0 -sin t cos t]
Orientation& Orientation::Pitch(float theta) {
    float c = cosf(theta), s = sinf(theta);
    float r[3][3] = {
        {    m_r[0][0],                     m_r[0][1],                     m_r[0][2]},
        {c*m_r[1][0] + s*m_r[2][0], c*m_r[1][1] + s*m_r[2][1], c*m_r[1][2] + s*m_r[2][2]},
        {c*m_r[2][0] - s*m_r[1][0], c*m_r[2][1] - s*m_r[1][1], c*m_r[2][2] - s*m_r[1][2]},
    };
    *this = r; return *this;
}

// Rotate about local YAW axis (Y) by theta.
//        [cos t 0 -sin t]
// this = [    0 1      0] * this
//        [sin t 0  cos t]
Orientation& Orientation::Yaw(float theta) {
    float c = cosf(theta), s = sinf(theta);
    float r[3][3] = {
        {c*m_r[0][0] - s*m_r[2][0], c*m_r[0][1] - s*m_r[2][1], c*m_r[0][2] - s*m_r[2][2]},
        {    m_r[1][0],                     m_r[1][1],                     m_r[1][2]},
        {c*m_r[2][0] + s*m_r[0][0], c*m_r[2][1] + s*m_r[0][1], c*m_r[2][2] + s*m_r[0][2]},
    };
    *this = r; return *this;
}

// Rotate about local ROLL axis (Z) by theta.
//        [ cos t sin t 0]
// this = [-sin t cos t 0] * this
//        [     0     0 1]
Orientation& Orientation::Roll(float theta) {
    float c = cosf(theta), s = sinf(theta);
    float r[3][3] = {
        {c*m_r[0][0] + s*m_r[1][0], c*m_r[0][1] + s*m_r[1][1], c*m_r[0][2] + s*m_r[1][2]},
        {c*m_r[1][0] - s*m_r[0][0], c*m_r[1][1] - s*m_r[0][1], c*m_r[1][2] - s*m_r[0][2]},
        {    m_r[2][0],                     m_r[2][1],                     m_r[2][2]},
    };
    *this = r; return *this;
}

// Re-orthonormalize after accumulating rotations (prevents matrix drift).
void Orientation::Renormalize(void) {
    Vector forward = GetForward();
    Vector up      = GetUp();
    Set(forward, up);
}

// Rebuild an orthonormal basis from a forward + up hint (Gram-Schmidt via cross products).
bool Orientation::Set(const Vector& forwardAxis, const Vector& upAxis) {
    Vector rightAxis = CrossProduct(forwardAxis, upAxis);
    float lRight   = rightAxis.Length();
    float lForward = forwardAxis.Length();
    SetUp(CrossProduct(rightAxis, forwardAxis));
    float lUp = GetUp().Length();
    if (lRight != 0.0f && lUp != 0.0f && lForward != 0.0f) {
        SetForward(forwardAxis / lForward);   // stores -forward into row 2
        SetUp(GetUp() / lUp);
        SetRight(rightAxis / lRight);
        return true;
    }
    Reset();
    return false;
}
