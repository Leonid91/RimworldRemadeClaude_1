// Water: refraction of the bed through depth-based absorption, two scrolling normal maps (rivers scroll along their
// flow, carried in the vertex colour), soft shore foam from the depth buffer, rain ripples, sun glints.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D normal_a : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_b : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear;
uniform vec3 shallow_color : source_color = vec3(0.22, 0.42, 0.40);
uniform vec3 deep_color : source_color = vec3(0.03, 0.10, 0.14);
uniform float absorption = 0.9;

varying vec3 wpos;
varying vec2 flow;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	flow = (COLOR.rg * 2.0 - 1.0) * COLOR.b * 2.0; // river flow direction * speed (0 for still water)
	// gentle swell on open water
	float still = 1.0 - clamp(length(flow) * 3.0, 0.0, 1.0);
	VERTEX.y += (sin(wpos.x * 0.6 + TIME * 1.1) + sin(wpos.z * 0.45 - TIME * 0.9)) * 0.025 * still * (0.4 + wind_strength);
}

void fragment() {
	vec2 p = wpos.xz;
	float t = TIME;
	// wind drift is uniform; river flow varies in space, so it uses the two-phase flow-map technique
	// (two layers restarting half a cycle apart, cross-faded) to avoid stretching over time
	vec2 wind_drift = wind_dir * t * 0.05 * (0.3 + wind_strength);
	float ph0 = fract(t * 0.35), ph1 = fract(t * 0.35 + 0.5);
	float blend = abs(1.0 - 2.0 * ph0);
	vec2 f = flow * 1.6;
	vec3 a0 = texture(normal_a, p * 0.11 + wind_drift - f * ph0).xyz;
	vec3 a1 = texture(normal_a, p * 0.11 + wind_drift - f * ph1 + vec2(0.5, 0.21)).xyz;
	vec3 b0 = texture(normal_b, p * 0.23 - wind_drift * 0.6 - f * ph0 * 1.3 + vec2(0.37, 0.11)).xyz;
	vec3 b1 = texture(normal_b, p * 0.23 - wind_drift * 0.6 - f * ph1 * 1.3 + vec2(0.12, 0.64)).xyz;
	vec3 n1 = mix(a1, a0, blend) * 2.0 - 1.0;
	vec3 n2 = mix(b1, b0, blend) * 2.0 - 1.0;
	vec3 n = normalize(vec3((n1.xy + n2.xy) * 0.6, 2.4 - wind_strength * 0.8));
	// rain ripples
	if (wetness > 0.0) {
		vec2 cell = floor(p * 2.0);
		float h = hash12(cell);
		float ph = fract(t * 1.3 + h);
		vec2 c = (cell + 0.5 + (vec2(hash12(cell + 3.1), hash12(cell + 7.7)) - 0.5) * 0.6) / 2.0;
		float d = length(p - c);
		float ring = sin((d - ph * 0.5) * 60.0) * smoothstep(0.25, 0.0, abs(d - ph * 0.5)) * (1.0 - ph);
		n.xy += normalize(p - c + 1e-4) * ring * 0.25 * clamp(wetness * 1.5, 0.0, 1.0);
	}
	NORMAL_MAP = n * 0.5 + 0.5;
	NORMAL_MAP_DEPTH = 1.0;

	// depth of water under this pixel (scene depth behind the surface)
	float depth_raw = texture(depth_tex, SCREEN_UV).r;
	vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, depth_raw, 1.0);
	vec4 view = INV_PROJECTION_MATRIX * ndc;
	view.xyz /= view.w;
	float scene_dist = -view.z;
	float surf_dist = -VERTEX.z;
	float thickness = max(scene_dist - surf_dist, 0.0);
	// refraction: offset the screen lookup by the normal, less where the water is thin
	vec2 refr_uv = SCREEN_UV + n.xy * 0.02 * clamp(thickness, 0.0, 1.0);
	vec3 bed = textureLod(screen_tex, refr_uv, 0.0).rgb;
	float a = 1.0 - exp(-thickness * absorption);
	vec3 water = mix(shallow_color, deep_color, clamp(thickness * 0.35, 0.0, 1.0)) * (0.35 + 0.65 * daylight);
	vec3 col = mix(bed, water, clamp(a * 0.9 + 0.08, 0.0, 1.0));
	// shore foam
	float foam = smoothstep(0.1, 0.0, thickness) * (0.5 + 0.5 * vnoise2(p * 6.0 + t * vec2(0.4, 0.3))) * 0.6;
	foam += length(flow) * smoothstep(0.6, 0.9, mix(vnoise2(p * 3.0 - f * ph1), vnoise2(p * 3.0 - f * ph0 + 3.7), blend)) * 0.3;
	col = mix(col, vec3(0.86, 0.90, 0.88) * (0.4 + 0.6 * daylight), clamp(foam, 0.0, 1.0) * 0.7);
	col = mix(col, vec3(0.85, 0.9, 0.95), snow_cover * smoothstep(0.1, 0.0, thickness) * 0.5);
	EMISSION = col * 0.65; // refracted light is already lit: emit it, let the surface add reflections
	ALBEDO = col * 0.35;
	ROUGHNESS = 0.12 + wetness * 0.05;
	SPECULAR = 0.55;
	METALLIC = 0.0;
	ALPHA = clamp(0.55 + a * 0.45 + foam, 0.0, 1.0);
}
