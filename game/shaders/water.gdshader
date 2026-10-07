// Water (look of the first prototype, RimworldTest): normals from two scrolling noise height fields, colour from the
// depth of water under each pixel (shallow teal → deep blue-green), alpha by depth so the bed shows through the
// shallows, shoreline foam, cloud shadows. Every layer scrolls continuously (no flow-map phase resets, which read as
// a stop-go stutter). River water (vertex colour b) drifts downstream along the map's river direction.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform vec3 shallow_col : source_color = vec3(0.30, 0.58, 0.52);
uniform vec3 deep_col : source_color = vec3(0.06, 0.22, 0.28);
uniform vec2 river_dir = vec2(0.0, 1.0); // map space, unit
uniform float river_speed = 0.35;        // texture units per second along river_dir

varying vec3 wpos;
varying float river;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	river = clamp(COLOR.b * 4.0, 0.0, 1.0);
	float w = sin(wpos.x * 0.8 + TIME * 1.1) * cos(wpos.z * 0.7 + TIME * 0.9);
	VERTEX.y += w * 0.015 * (1.0 - river * 0.6);
}

// height of two scrolling noise layers and its slope
vec3 heights(vec2 uv1, vec2 uv2) {
	float h1 = texture(noise_tex, uv1).r;
	float h2 = texture(noise_tex, uv2).r;
	float hx = texture(noise_tex, uv1 + vec2(0.01, 0.0)).r - h1 + texture(noise_tex, uv2 + vec2(0.01, 0.0)).r - h2;
	float hz = texture(noise_tex, uv1 + vec2(0.0, 0.01)).r - h1 + texture(noise_tex, uv2 + vec2(0.0, 0.01)).r - h2;
	return vec3(hx, h1 + h2, hz);
}

void fragment() {
	// scene depth below the surface (vertical, in metres)
	float d = texture(depth_tex, SCREEN_UV).r;
	vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, d, 1.0);
	vec4 view = INV_PROJECTION_MATRIX * ndc;
	view.xyz /= view.w;
	vec3 scene_w = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;
	float depth = max(wpos.y - scene_w.y, 0.0);

	// still water: slow drift with the wind
	vec2 drift = wind_dir * 0.02 * TIME;
	vec3 hs = heights(wpos.xz * 0.09 + TIME * vec2(0.012, 0.007) + drift, wpos.xz * 0.13 - TIME * vec2(0.009, -0.011));
	vec3 g = hs;
	if (river > 0.001) {
		// river water: the same layers carried downstream at two speeds
		vec2 down = river_dir * river_speed * TIME;
		vec3 hr = heights(wpos.xz * 0.09 - down * 0.09 + drift, wpos.xz * vec2(0.13, 0.16) - down * 0.16 + vec2(0.37, 0.11));
		g = mix(hs, hr, river);
	}
	float k = 2.0 + wind_strength * 2.5 + river * 1.5;
	vec3 n = normalize(vec3(-g.x * k, 1.0, -g.z * k));
	// rain ripples
	if (wetness > 0.0) {
		vec2 p = wpos.xz;
		vec2 cell = floor(p * 2.0);
		float h = hash12(cell);
		float ph = fract(TIME * 1.3 + h);
		vec2 c = (cell + 0.5 + (vec2(hash12(cell + 3.1), hash12(cell + 7.7)) - 0.5) * 0.08) / 2.0;
		float dd = length(p - c);
		float rr = ph * 0.18;
		float ring = sin((dd - rr) * 70.0) * smoothstep(0.06, 0.0, abs(dd - rr)) * (1.0 - ph) * step(dd, 0.24);
		n.xz += normalize(p - c + 1e-4) * ring * 0.25 * clamp(wetness * 1.5, 0.0, 1.0);
		n = normalize(n);
	}
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);

	float df = 1.0 - exp(-depth * 2.6);
	vec3 col = mix(shallow_col, deep_col, df);
	// shoreline foam, and a little streaking on the river
	float foamN = texture(noise_tex, wpos.xz * 0.5 + TIME * 0.02).g;
	float foam = smoothstep(0.12, 0.0, depth - foamN * 0.08) * (0.6 + 0.4 * sin(TIME * 1.5 + wpos.x + wpos.z));
	foam += river * smoothstep(0.62, 0.8, texture(noise_tex, wpos.xz * vec2(0.35, 0.35) - river_dir * river_speed * TIME * 0.35).g) * 0.25;
	col = mix(col, vec3(0.75, 0.82, 0.82), clamp(foam, 0.0, 1.0) * 0.55);
	col = mix(col, vec3(0.80, 0.84, 0.90), snow_cover * smoothstep(0.1, 0.0, depth) * 0.5);
	col *= cloud_shadow(wpos.xz) * 0.4 + 0.6;

	ALBEDO = col;
	ALPHA = clamp(0.12 + df * 0.88 + foam * 0.5, 0.0, 0.94);
	ROUGHNESS = mix(0.07, 0.14, wetness);
	METALLIC = 0.0;
	SPECULAR = 0.7;
}
