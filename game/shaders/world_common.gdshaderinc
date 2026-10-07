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

// green foliage tint across the year: fresh spring, deep summer, golden autumn, dull winter
vec3 season_foliage(vec3 summer_col, float variation) {
	float s = mod(season, 4.0);
	vec3 spring = summer_col * vec3(1.1, 1.2, 0.8) + vec3(0.006, 0.01, 0.0);
	vec3 autumn = mix(vec3(0.30, 0.09, 0.012), vec3(0.45, 0.22, 0.02), variation);
	vec3 winter = vec3(0.08, 0.06, 0.03);
	if (s < 1.0) return mix(spring, summer_col, s);
	if (s < 2.0) return mix(summer_col, autumn, smoothstep(1.5, 2.0, s) * (0.6 + 0.4 * variation));
	if (s < 3.0) return mix(autumn, winter, smoothstep(2.4, 3.0, s));
	return mix(winter, spring, smoothstep(3.6, 4.0, s));
}

// grass colour across the year
vec3 season_grass(vec3 base) {
	float s = mod(season, 4.0);
	vec3 dry = base * vec3(1.25, 1.05, 0.55);
	vec3 dead = vec3(0.36, 0.31, 0.20);
	if (s < 1.0) return mix(base * vec3(0.95, 1.12, 0.85), base, s);
	if (s < 2.0) return mix(base, dry, smoothstep(1.3, 2.0, s) * 0.7);
	if (s < 3.0) return mix(dry, dead, smoothstep(2.2, 3.0, s));
	return mix(dead, base * vec3(0.95, 1.12, 0.85), smoothstep(3.5, 4.0, s));
}

vec2 wind_sway(vec3 world_pos, float height, float stiffness) {
	float t = TIME;
	float gust = 0.6 + 0.4 * sin(dot(world_pos.xz, wind_dir) * 0.08 - t * 1.3) * sin(t * 0.37 + world_pos.x * 0.01);
	float flutter = sin(t * 2.7 + world_pos.x * 0.7 + world_pos.z * 0.9) * 0.25;
	float amount = wind_strength * gust * height * height / stiffness;
	return wind_dir * amount + vec2(-wind_dir.y, wind_dir.x) * flutter * amount * 0.4;
}
