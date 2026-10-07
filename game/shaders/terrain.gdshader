// Ground: blends procedural terrain textures between neighbouring cells with a noisy bilinear weighting, adds the
// grass underlay, wet darkening and puddles after rain, snow cover and drifting cloud shadows.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2DArray terrain_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float tex_scale = 0.22; // texture repeats per metre

varying vec3 wpos;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec4 cell_data(vec2 cell) {
	cell = clamp(cell, vec2(0.0), map_size - 1.0);
	return texelFetch(map_data, ivec2(cell), 0);
}

float id_of(vec4 d) { return floor(d.r * 255.0 + 0.5); }

void fragment() {
	vec2 p = wpos.xz;
	// jitter the lookup so cell borders become organic
	vec2 jit = vec2(fbm2(p * 0.9), fbm2(p * 0.9 + 17.0)) - 0.5;
	vec2 q = p + jit * 0.9 - 0.5;
	vec2 c0 = floor(q);
	vec2 f = q - c0;
	f = smoothstep(0.15, 0.85, f);
	vec2 uv = p * tex_scale;
	vec4 d00 = cell_data(c0), d10 = cell_data(c0 + vec2(1, 0)), d01 = cell_data(c0 + vec2(0, 1)), d11 = cell_data(c0 + vec2(1, 1));
	float id00 = id_of(d00), id10 = id_of(d10), id01 = id_of(d01), id11 = id_of(d11);
	vec4 t00 = texture(terrain_tex, vec3(uv, id00));
	vec4 t10 = texture(terrain_tex, vec3(uv, id10));
	vec4 t01 = texture(terrain_tex, vec3(uv, id01));
	vec4 t11 = texture(terrain_tex, vec3(uv, id11));
	// height-aware blend: pebbles poke through soil at the transitions
	vec4 w = vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
	w *= vec4(t00.a, t10.a, t01.a, t11.a) + 0.35;
	w = pow(w, vec4(2.0));
	w /= dot(w, vec4(1.0));
	vec3 col = t00.rgb * w.x + t10.rgb * w.y + t01.rgb * w.z + t11.rgb * w.w;
	// large-scale variation breaks texture repetition
	float macro = fbm2(p * 0.035);
	col *= 0.82 + macro * 0.36;

	// grass underlay (the 3D blades grow on top of it)
	vec2 fl = q - c0;
	float grass = mix(mix(d00.g, d10.g, fl.x), mix(d01.g, d11.g, fl.x), fl.y);
	vec3 gcol = season_grass(mix(vec3(0.20, 0.28, 0.09), vec3(0.28, 0.33, 0.12), fbm2(p * 0.3)));
	col = mix(col, gcol, smoothstep(0.08, 0.6, grass) * 0.85);

	float rough = 0.92;
	// wet ground: darker, glossier; puddles collect in low noise basins on flat soil
	float wet = wetness;
	col *= 1.0 - wet * 0.35;
	rough -= wet * 0.35;
	float puddle = smoothstep(0.62, 0.7, fbm2(p * 0.18 + 3.0)) * smoothstep(0.35, 0.8, wet) * (1.0 - grass);
	col = mix(col, col * 0.35 + vec3(0.02, 0.025, 0.03), puddle * 0.8);
	rough = mix(rough, 0.05, puddle);

	// snow
	float sn = snow_cover * smoothstep(0.25, 0.75, fbm2(p * 0.25) * 0.6 + snow_cover * 0.8);
	col = mix(col, vec3(0.88, 0.90, 0.94), sn);
	rough = mix(rough, 0.6, sn);

	col *= cloud_shadow(p);
	ALBEDO = col;
	ROUGHNESS = rough;
	SPECULAR = 0.35 + puddle * 0.4;
}
