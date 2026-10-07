// Shared world-shader globals and helpers (set from C# every frame by Lighting / GameView).
global uniform float daylight;      // 0 night .. 1 noon
global uniform vec2 wind_dir;       // map space unit vector (x east, z south)
global uniform float wind_strength; // 0..1.5
global uniform vec2 cloud_offset;   // drifting cloud-shadow offset (world units)
global uniform float cloud_cover;   // 0..1
global uniform float wetness;       // 0..1 ground wetness after rain
global uniform float snow_cover;    // 0..1
global uniform float season;        // 0 spring .. 1 summer .. 2 autumn .. 3 winter .. 4 (wraps)
global uniform vec3 focus_pos;      // camera focus point (world)
global uniform vec4 reveal_pos;     // xyz + radius: canopies fade around the controlled colonist
global uniform vec2 map_size;
global uniform sampler2D map_data : filter_nearest, repeat_disable; // r terrain id/255, g grass, b rock, a unused

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1, 0)), f.x), mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), f.x), f.y);
}

float fbm2(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 4; i++) { s += a * vnoise2(p); p = p * 2.02 + 7.3; a *= 0.5; }
	return s;
}

// 0 = full cloud shadow, 1 = sunlit
float cloud_shadow(vec2 world_xz) {
	float n = fbm2((world_xz + cloud_offset) * 0.012);
	float c = smoothstep(1.0 - cloud_cover * 0.9 - 0.15, 1.0 - cloud_cover * 0.9 + 0.2, n);
	return 1.0 - c * 0.55 * clamp(daylight * 2.0, 0.0, 1.0);
}

// how far autumn colours have progressed (0 = green .. 1 = full autumn), and how bare the crowns are in winter
float autumn_amount() {
	float s = mod(season, 4.0);
	return smoothstep(2.05, 2.6, s) * (1.0 - smoothstep(3.7, 3.95, s));
}
float winter_amount() {
	float s = mod(season, 4.0);
	return smoothstep(2.85, 3.15, s) * (1.0 - smoothstep(3.7, 3.95, s));
}

// Tree / bush sway (first prototype). Displacement grows with height above the instance origin, so trunks and leaves
// stay attached. ntex = the shared RGBA noise texture (alpha: low-frequency gusts).
vec3 tree_sway(sampler2D ntex, vec3 world_v, vec3 origin, float phase, float amount) {
	float hgt = max(world_v.y - origin.y, 0.0);
	float gust = texture(ntex, origin.xz * 0.02 - wind_dir * TIME * 0.045).a;
	float k = hgt * hgt * 0.012 * amount;
	vec2 sway = wind_dir * (gust * 1.3 + 0.25) * wind_strength * k * 2.2;
	sway += vec2(sin(TIME * 1.25 + phase), cos(TIME * 1.05 + phase * 1.3)) * k * 0.7 * wind_strength;
	return vec3(sway.x, -length(sway) * 0.15, sway.y);
}

// 1 when this fragment sits between the camera and the focused colonist (canopy x-ray); reveal_pos.w = radius.
float reveal_cut(vec3 wp, vec3 cam) {
	if (reveal_pos.w <= 0.0) return 0.0;
	vec3 a = cam, b = reveal_pos.xyz + vec3(0.0, 0.8, 0.0);
	vec3 ab = b - a;
	float t = clamp(dot(wp - a, ab) / dot(ab, ab), 0.0, 1.0);
	float d = length(a + ab * t - wp);
	// only foliage above head height: bushes beside the colonist stay whole
	return (1.0 - smoothstep(reveal_pos.w * 0.6, reveal_pos.w, d)) * step(0.3, t) * step(t, 0.995) * step(reveal_pos.y + 1.5, wp.y);
}
