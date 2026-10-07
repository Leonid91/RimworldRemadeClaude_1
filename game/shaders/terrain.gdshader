// Ground (look of the first prototype, RimworldTest): procedural colours per terrain type from one RGBA noise
// texture, an organic blend between the four nearest cells (noise-wobbled), a smooth grass-cover tint, caustics
// under water, puddles after rain, snow, drifting cloud shadows. Colours are authored in sRGB.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
uniform float far_view = 0.0; // 1 when zoomed out beyond the grass blades: the meadow tint carries the colour

varying vec3 wpos;
varying vec3 wnrm;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnrm = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
}

vec4 cell(ivec2 c) {
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	return texelFetch(map_data, c, 0);
}
int tid(vec4 d) { return int(d.r * 255.0 + 0.5); }

vec3 stone_col(vec3 w, vec4 nm, vec4 nd) {
	vec3 c = vec3(0.44, 0.41, 0.40);
	float strata = texture(noise_tex, vec2(w.y * 0.55 + nm.r * 0.35, (w.x + w.z) * 0.004)).r;
	c *= 0.66 + 0.62 * strata;
	c *= 0.78 + 0.4 * nd.b * nd.r;
	float crack = smoothstep(0.62, 0.9, texture(noise_tex, w.xz * 0.11 + w.y * 0.05).g);
	c *= 1.0 - crack * 0.45;
	c += (texture(noise_tex, w.xz * 1.3).b - 0.5) * 0.12;
	return c;
}

// Terrain ids (Remade.Map.Terrain): 0 soil, 1 rich soil, 2 gravel, 3 sand, 4 mud, 5 marsh, 6 rough granite,
// 7 river shallow, 8 river deep, 9 ocean shallow, 10 ocean deep, 11 lake shallow, 12 lake deep.
vec3 terrain_col(int t, vec2 p, vec4 nm, vec4 nd, vec4 nd2, out float rough, out float poros) {
	rough = 0.92; poros = 1.0;
	if (t == 0 || t == 1) { // soil / rich soil
		float rich = float(t == 1);
		vec3 a = mix(vec3(0.34, 0.25, 0.16), vec3(0.19, 0.14, 0.09), rich);
		vec3 b = mix(vec3(0.44, 0.34, 0.22), vec3(0.27, 0.19, 0.12), rich);
		vec3 c = mix(a, b, nm.r);
		c *= 0.82 + 0.34 * nd.b * nd2.r;
		float peb = smoothstep(0.14, 0.06, nd.g) * step(0.68, nd2.a);
		c = mix(c, c * 1.35 + vec3(0.03), peb * (1.0 - rich) * 0.5);
		return c;
	}
	if (t == 3 || t == 9) { // sand, sandy sea bed
		vec3 c = mix(vec3(0.68, 0.58, 0.42), vec3(0.79, 0.70, 0.53), nm.r);
		float rip = sin(p.x * 3.2 + p.y * 1.1 + nm.r * 9.0) * 0.5 + 0.5;
		c *= 0.93 + 0.07 * rip + 0.08 * (nd.b - 0.5);
		rough = 0.95; poros = 0.8;
		return c;
	}
	if (t == 2) { // gravel
		vec3 c = mix(vec3(0.40, 0.37, 0.33), vec3(0.52, 0.49, 0.45), nm.r);
		float stone = smoothstep(0.30, 0.1, nd.g) * smoothstep(0.25, 0.6, nd2.r + nd.a * 0.4);
		vec3 sc = c * mix(1.02, 1.28, nd.a) * mix(vec3(1.0), vec3(1.05, 1.0, 0.94), nd2.b);
		c = mix(c * 0.82, sc, stone * 0.85);
		c *= 0.88 + 0.24 * nd2.b;
		rough = 0.85; poros = 0.6;
		return c;
	}
	if (t == 4 || t == 5) { // mud, marsh
		vec3 c = mix(vec3(0.20, 0.18, 0.12), vec3(0.29, 0.26, 0.16), nm.r);
		c *= 0.85 + 0.3 * nd.b;
		if (t == 5) c = mix(c, vec3(0.22, 0.26, 0.12), 0.45 * smoothstep(0.4, 0.7, nd2.r));
		rough = mix(0.6, 0.25, smoothstep(0.55, 0.75, nm.a));
		return c;
	}
	if (t == 6) { // rough granite
		vec3 c = stone_col(vec3(p.x, nm.r * 2.0, p.y), nm, nd) * 0.95;
		float crack = smoothstep(0.035, 0.0, abs(nd2.g - 0.5) * 0.12 + nd.g * 0.02 - 0.01);
		c *= 1.0 - crack * 0.25;
		rough = 0.8; poros = 0.4;
		return c;
	}
	if (t == 7 || t == 11) { // shallow fresh-water bed
		vec3 c = mix(vec3(0.40, 0.36, 0.26), vec3(0.50, 0.45, 0.32), nm.r);
		c *= 0.85 + 0.25 * nd.b;
		return c;
	}
	// deep water bed
	return mix(vec3(0.16, 0.17, 0.12), vec3(0.24, 0.24, 0.17), nm.r);
}

void fragment() {
	vec2 p = wpos.xz;
	vec4 nm = texture(noise_tex, p * 0.031);
	vec4 nd = texture(noise_tex, p * 0.23);
	vec2 pr = vec2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8);
	vec4 nd2 = texture(noise_tex, pr * 0.137 + 0.31);

	// organic blend between the 4 nearest cell centres
	vec2 q = p - 0.5;
	ivec2 b = ivec2(floor(q));
	vec2 fl = fract(q);
	vec2 wob = (vec2(nm.g, nd2.r) - 0.5) * 0.55 + (nd.r - 0.5) * 0.25;
	vec2 f = smoothstep(0.18, 0.82, clamp(fl + wob, 0.0, 1.0));
	vec4 d00 = cell(b), d10 = cell(b + ivec2(1, 0)), d01 = cell(b + ivec2(0, 1)), d11 = cell(b + ivec2(1, 1));
	int t00 = tid(d00), t10 = tid(d10), t01 = tid(d01), t11 = tid(d11);
	float rough, poros;
	vec3 col;
	if (t00 == t10 && t00 == t01 && t00 == t11) {
		col = terrain_col(t00, p, nm, nd, nd2, rough, poros);
	} else {
		float r0, r1, r2, r3, q0, q1, q2, q3;
		vec3 a = terrain_col(t00, p, nm, nd, nd2, r0, q0);
		vec3 bb = terrain_col(t10, p, nm, nd, nd2, r1, q1);
		vec3 c = terrain_col(t01, p, nm, nd, nd2, r2, q2);
		vec3 d = terrain_col(t11, p, nm, nd, nd2, r3, q3);
		col = mix(mix(a, bb, f.x), mix(c, d, f.x), f.y);
		rough = mix(mix(r0, r1, f.x), mix(r2, r3, f.x), f.y);
		poros = mix(mix(q0, q1, f.x), mix(q2, q3, f.x), f.y);
	}

	// grass cover tint (smooth, bilinear over the 4 cells); dries in late summer and autumn
	float g = mix(mix(d00.g, d10.g, fl.x), mix(d01.g, d11.g, fl.x), fl.y);
	float s4 = mod(season, 4.0);
	float season_dry = smoothstep(1.4, 2.0, s4) * (1.0 - smoothstep(3.4, 3.9, s4)) * 0.6;
	float gm = g * (0.55 + 0.45 * smoothstep(0.3, 0.7, nd2.r));
	vec3 gcol = mix(vec3(0.17, 0.25, 0.07), vec3(0.32, 0.38, 0.13), nm.a);
	gcol = mix(gcol, vec3(0.45, 0.38, 0.18), season_dry);
	col = mix(col, gcol * (0.85 + 0.3 * nd.b), clamp(gm * 0.85, 0.0, 0.85));

	// under water: darker, cooler + caustics
	float uw = smoothstep(-0.08, -0.55, wpos.y);
	if (uw > 0.0) {
		col = mix(col, col * vec3(0.55, 0.72, 0.72), uw);
		float cz = texture(noise_tex, p * 0.18 + vec2(TIME * 0.011, TIME * 0.007)).r;
		float cz2 = texture(noise_tex, p * 0.21 - vec2(TIME * 0.009, -TIME * 0.012)).r;
		float caus = pow(1.0 - abs(cz - cz2), 14.0);
		col += caus * 0.22 * daylight * uw;
		rough = 0.5;
	}

	// snow (not under water)
	float sn = snow_cover * smoothstep(0.25, 0.75, nm.r * 0.6 + nd.b * 0.4 + snow_cover * 0.5) * smoothstep(0.55, 0.85, wnrm.y) * (1.0 - uw);
	vec3 snc = vec3(0.8, 0.83, 0.89) * (0.92 + 0.12 * nd.r);
	col = mix(col, snc, sn);
	rough = mix(rough, 0.55, sn);
	poros = mix(poros, 0.0, sn);

	// zoomed far out the grass blades are gone: let the meadow tint carry the colour
	if (far_view > 0.0) {
		col = mix(col, mix(vec3(0.24, 0.33, 0.10), vec3(0.36, 0.42, 0.16), nm.a) * (1.0 - sn) + snc * sn, g * far_view * 0.55 * (1.0 - uw));
	}

	// wet ground: darker and glossier; puddles collect in low noise basins on bare, flat ground
	float wet = wetness * poros;
	col *= mix(1.0, 0.6, wet);
	rough = mix(rough, 0.22, wetness * (0.4 + 0.6 * poros));
	float puddle = smoothstep(0.62, 0.7, nm.a * 0.7 + nd2.r * 0.3) * smoothstep(0.35, 0.8, wetness) * (1.0 - g) * (1.0 - uw) * poros;
	col = mix(col, col * 0.35 + vec3(0.02, 0.025, 0.03), puddle * 0.8);
	rough = mix(rough, 0.05, puddle);

	col *= cloud_shadow(p) * 0.6 + 0.4;
	ALBEDO = pow(max(col, vec3(0.0)), vec3(2.2));
	ROUGHNESS = rough;
	SPECULAR = 0.35 + puddle * 0.4;
}
