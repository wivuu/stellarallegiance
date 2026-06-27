using System;
using Godot;

// A warp gate ("aleph"), rendered as a curved funnel mesh with a custom shader that
// creates an animated swirling vortex effect — like water draining into a plughole.
// The funnel geometry is an actual surface of revolution (curved inward like a
// trumpet/whirlpool). A vertex+fragment shader animates swirling UV flow lines,
// increases brightness toward the throat (center), and fades alpha to zero at the
// mouth rim. Purely cosmetic: position set by WorldRenderer, orientation faces sector
// center.
public partial class AlephView : Node3D
{
    private const float MouthRadius = 16f;
    private const float ThroatRadius = 2.0f;
    private const float FunnelDepth = 28f;
    private const int Rings = 40; // vertical subdivisions
    private const int Segments = 48; // around the circumference

    public override void _Ready()
    {
        // Build the funnel mesh and shader-driven material
        var mesh = BuildFunnelMesh();
        var material = BuildVortexShader();

        var meshInst = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(meshInst);

        // --- Cap the throat with a small disc so there's no hole ---
        var capMesh = BuildCapDisc();
        var capMat = new ShaderMaterial { Shader = BuildCapShader() };
        capMat.SetShaderParameter("color_core", new Vector3(0.7f, 0.98f, 1.0f));
        var capInst = new MeshInstance3D
        {
            Mesh = capMesh,
            MaterialOverride = capMat,
            Position = new Vector3(0, -FunnelDepth, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(capInst);

        // --- Bright spinning star at the very center of the throat ---
        var starShaderMat = new ShaderMaterial { Shader = BuildStarShader() };
        starShaderMat.SetShaderParameter("color_core", new Vector3(0.8f, 1.0f, 1.0f));
        starShaderMat.SetShaderParameter("spin_speed", 2.0f);
        var starQuad = new QuadMesh { Size = new Vector2(ThroatRadius * 3.5f, ThroatRadius * 3.5f) };
        starQuad.Material = starShaderMat;
        var starInst = new MeshInstance3D
        {
            Mesh = starQuad,
            Position = new Vector3(0, -FunnelDepth, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(starInst);

        // Bright core glow at the throat
        var light = new OmniLight3D
        {
            LightColor = new Color(0.5f, 0.95f, 1.0f),
            LightEnergy = 3.0f,
            OmniRange = ThroatRadius * 5f,
            OmniAttenuation = 1.5f,
            Position = new Vector3(0, -FunnelDepth * 0.9f, 0),
            ShadowEnabled = false,
        };
        AddChild(light);
    }

    /// <summary>
    /// Builds a curved funnel (surface of revolution). The profile curve goes from
    /// MouthRadius at the top (y=0) to ThroatRadius at the bottom (y=-FunnelDepth),
    /// following a power curve so it flares outward like a trumpet/whirlpool.
    /// UV.x = angle around circumference (0-1), UV.y = depth (0=mouth, 1=throat).
    /// </summary>
    private ArrayMesh BuildFunnelMesh()
    {
        var verts = new Vector3[(Rings + 1) * (Segments + 1)];
        var uvs = new Vector2[(Rings + 1) * (Segments + 1)];
        var normals = new Vector3[(Rings + 1) * (Segments + 1)];
        var indices = new int[Rings * Segments * 6];

        for (int ring = 0; ring <= Rings; ring++)
        {
            float t = ring / (float)Rings; // 0 at mouth, 1 at throat
            // Power curve for whirlpool shape: flares wide at top, tight curve inward
            float radius = MouthRadius * Mathf.Pow(1f - t, 2.2f) + ThroatRadius;
            float y = -t * FunnelDepth;

            for (int seg = 0; seg <= Segments; seg++)
            {
                float u = seg / (float)Segments;
                float angle = u * Mathf.Tau;
                int idx = ring * (Segments + 1) + seg;

                verts[idx] = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
                uvs[idx] = new Vector2(u, t);

                // Normal pointing outward from funnel surface
                var outward = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                normals[idx] = outward.Normalized();
            }
        }

        int triIdx = 0;
        for (int ring = 0; ring < Rings; ring++)
        {
            for (int seg = 0; seg < Segments; seg++)
            {
                int tl = ring * (Segments + 1) + seg;
                int tr = tl + 1;
                int bl = tl + (Segments + 1);
                int br = bl + 1;

                indices[triIdx++] = tl;
                indices[triIdx++] = bl;
                indices[triIdx++] = tr;

                indices[triIdx++] = tr;
                indices[triIdx++] = bl;
                indices[triIdx++] = br;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var arrMesh = new ArrayMesh();
        arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return arrMesh;
    }

    /// <summary>
    /// Shader that creates the animated swirling vortex look:
    /// - Swirling flow lines that rotate faster toward the throat
    /// - Brightness increases toward center (UV.y -> 1)
    /// - Alpha fades to 0 at the mouth rim (UV.y -> 0)
    /// - Multiple layered noise-like patterns for organic water/energy feel
    /// - Double-sided rendering so you can see inside the funnel
    /// - Noise is sampled on a circular path to avoid UV seam at angle=0/1
    /// </summary>
    private ShaderMaterial BuildVortexShader()
    {
        var shader = new Shader();
        shader.Code =
            @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add;

uniform float time_scale : hint_range(0.1, 5.0) = 1.0;
uniform vec3 color_core = vec3(0.6, 0.98, 1.0);
uniform vec3 color_mid = vec3(0.25, 0.7, 1.0);
uniform vec3 color_edge = vec3(0.15, 0.4, 0.8);
uniform float emission_strength = 3.0;

// Simple hash-based noise
float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Seamless FBM: each octave samples along a circular path in noise space,
// ensuring perfect wrap at angle 0/1 regardless of frequency scaling.
float seamless_fbm(float angle_01, float y_coord, float base_radius) {
    float v = 0.0;
    float amp = 0.5;
    float rad = base_radius;
    float y = y_coord;
    float a_rad = angle_01 * 6.2832;
    for (int i = 0; i < 4; i++) {
        vec2 p = vec2(cos(a_rad) * rad + y, sin(a_rad) * rad + y * 0.7);
        v += amp * noise(p);
        rad *= 2.1;
        y *= 2.1;
        amp *= 0.5;
    }
    return v;
}

void fragment() {
    float t = TIME * time_scale;
    // UV.x = angle (0-1), UV.y = depth (0=mouth, 1=throat)
    float depth = UV.y;
    float angle = UV.x;

    // Swirl speed increases toward the throat (quadratic ramp)
    float swirl_speed = 0.3 + depth * depth * 2.5;

    // Animated swirl: offset the angle by time, wrapping seamlessly
    float swirl_angle = fract(angle - t * swirl_speed / 6.2832);

    // First noise layer: sample on a circular path to avoid seam
    float spiral = seamless_fbm(swirl_angle, depth * 8.0 - t * 0.6, 2.0);

    // Second layer at different scale/speed for complexity
    float swirl_angle2 = fract(angle * 1.7 - t * swirl_speed * 0.7 / 6.2832 + 0.3);
    float spiral2 = seamless_fbm(swirl_angle2, depth * 5.0 - t * 0.9, 3.0);

    // Third layer: slow, large-scale variation to break up any residual patterning
    float swirl_angle3 = fract(angle * 0.5 + t * 0.1);
    float spiral3 = seamless_fbm(swirl_angle3, depth * 3.0 + t * 0.3, 4.5);

    // Combine layers
    float pattern = spiral * 0.45 + spiral2 * 0.35 + spiral3 * 0.2;
    // Add sharper bright streaks (spiral arms)
    float streaks = pow(spiral, 3.0) * 2.0;
    pattern += streaks * 0.3;

    // Brightness ramps up toward throat: faint at mouth, intense at center
    float intensity = smoothstep(0.0, 0.6, depth) * (0.4 + 0.6 * depth);
    // Extra bloom at the very throat
    float core_bloom = smoothstep(0.75, 1.0, depth) * 2.0;
    intensity += core_bloom;

    // Alpha fades to zero at the mouth edge
    // Smooth fade over the outer 40% of the mouth
    float alpha_fade = smoothstep(0.0, 0.4, depth);
    // Also fade slightly at the very throat to avoid a hard cap
    alpha_fade *= (1.0 - smoothstep(0.95, 1.0, depth) * 0.3);

    // Color gradient: edge -> mid -> core
    vec3 col = mix(color_edge, color_mid, smoothstep(0.0, 0.5, depth));
    col = mix(col, color_core, smoothstep(0.5, 1.0, depth));

    // Modulate color by the swirl pattern
    col *= (0.5 + pattern * 0.8);
    // Add bright emission in the streaks
    col += color_core * streaks * intensity * 0.5;

    ALBEDO = col;
    EMISSION = col * emission_strength * intensity;
    ALPHA = alpha_fade * (0.3 + pattern * 0.7) * clamp(intensity + 0.2, 0.0, 1.0);
}
";
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("time_scale", 1.0f);
        mat.SetShaderParameter("color_core", new Vector3(0.6f, 0.98f, 1.0f));
        mat.SetShaderParameter("color_mid", new Vector3(0.25f, 0.7f, 1.0f));
        mat.SetShaderParameter("color_edge", new Vector3(0.15f, 0.4f, 0.8f));
        mat.SetShaderParameter("emission_strength", 3.0f);
        return mat;
    }

    /// <summary>
    /// Small disc mesh to cap the throat opening. UV maps radially so the cap
    /// shader can do a radial glow.
    /// </summary>
    private ArrayMesh BuildCapDisc()
    {
        int segs = Segments;
        // Center vertex + ring of vertices
        var verts = new Vector3[segs + 2];
        var uvs = new Vector2[segs + 2];
        var normals = new Vector3[segs + 2];
        var indices = new int[segs * 3];

        // Center
        verts[0] = Vector3.Zero;
        uvs[0] = new Vector2(0.5f, 0.5f);
        normals[0] = Vector3.Up;

        for (int i = 0; i <= segs; i++)
        {
            float a = (i / (float)segs) * Mathf.Tau;
            float x = Mathf.Cos(a) * ThroatRadius;
            float z = Mathf.Sin(a) * ThroatRadius;
            verts[i + 1] = new Vector3(x, 0, z);
            uvs[i + 1] = new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f);
            normals[i + 1] = Vector3.Up;
        }

        for (int i = 0; i < segs; i++)
        {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = i + 1;
            indices[i * 3 + 2] = i + 2;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var arrMesh = new ArrayMesh();
        arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return arrMesh;
    }

    /// <summary>
    /// Shader for the throat cap disc: radial glow that's brightest at center,
    /// with a subtle swirl animation.
    /// </summary>
    private Shader BuildCapShader()
    {
        var shader = new Shader();
        shader.Code =
            @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add;

uniform vec3 color_core = vec3(0.7, 0.98, 1.0);

void fragment() {
    // UV is radial: (0.5, 0.5) = center
    vec2 centered = UV - vec2(0.5);
    float dist = length(centered) * 2.0; // 0 at center, 1 at edge

    // Bright center, fades to transparent at edge
    float glow = pow(1.0 - clamp(dist, 0.0, 1.0), 2.0);
    // Pulsing
    glow *= 0.8 + 0.2 * sin(TIME * 3.0);

    vec3 col = color_core * glow;
    ALBEDO = col;
    EMISSION = col * 5.0;
    ALPHA = glow;
}
";
        return shader;
    }

    /// <summary>
    /// Shader for the star sprite: renders a spinning multi-pointed star shape
    /// with soft glow, discarding pixels outside the star silhouette.
    /// Billboard mode is handled in the vertex shader.
    /// </summary>
    private Shader BuildStarShader()
    {
        var shader = new Shader();
        shader.Code =
            @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add;

uniform vec3 color_core = vec3(0.8, 1.0, 1.0);
uniform float spin_speed = 2.0;

void vertex() {
    // Billboard: always face camera
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
        INV_VIEW_MATRIX[0],
        INV_VIEW_MATRIX[1],
        INV_VIEW_MATRIX[2],
        MODEL_MATRIX[3]);
}

void fragment() {
    vec2 centered = UV * 2.0 - 1.0; // -1 to 1
    float dist = length(centered);

    // Rotate the coordinate for spinning
    float angle = atan(centered.y, centered.x) + TIME * spin_speed;

    // Star shape: modulate radius threshold by angle to create points
    // 6-pointed star with inner/outer radius ratio
    float star6 = 0.3 + 0.15 * cos(angle * 6.0);
    // 4-pointed star at different phase for a complex shape
    float star4 = 0.25 + 0.2 * cos(angle * 4.0 + TIME * spin_speed * 0.5);
    // Combine: take the maximum extent of both star shapes
    float star_radius = max(star6, star4);

    // Soft falloff from center
    float core_glow = exp(-dist * 3.0); // bright gaussian center
    // Star rays: bright where dist < star_radius, fading beyond
    float ray_intensity = smoothstep(star_radius + 0.15, star_radius - 0.05, dist);
    // Outer soft halo
    float halo = exp(-dist * 1.5) * 0.4;

    float brightness = core_glow + ray_intensity * 0.7 + halo;

    // Discard fully transparent pixels (outside the glow entirely)
    if (brightness < 0.01) {
        discard;
    }

    vec3 col = color_core * brightness;
    ALBEDO = col;
    EMISSION = col * 6.0;
    ALPHA = clamp(brightness, 0.0, 1.0);
}
";
        return shader;
    }
}
