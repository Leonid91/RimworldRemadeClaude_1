// Granite massifs: triplanar granite detail, horizontal strata on cliff faces, darker crevices near the base,
// moss and snow settling on upward-facing surfaces.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D granite : source_color, filter_linear_mipmap_anisotropic, repeat_enable;

varying vec3 wpos;
varying vec3 wnorm;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec4 triplanar(vec3 p, vec3 n) {
	vec3 b = pow(abs(n), vec3(4.0));
	b /= b.x + b.y + b.z;
	return texture(granite, p.zy * 0.35) * b.x + texture(granite, p.xz * 0.35) * b.y + texture(granite, p.xy * 0.35) * b.z;
}

void fragment() {
	vec3 n = normalize(wnorm);
	vec4 g = triplanar(wpos, n);
	vec3 col = g.rgb;
	float up = clamp(n.y, 0.0, 1.0);
	// strata bands on steep faces
	float strata = sin(wpos.y * 3.1 + fbm2(wpos.xz * 0.4) * 4.0) * 0.5 + 0.5;
	col *= mix(1.0, 0.82 + strata * 0.25, 1.0 - up);
	// weathering darkens crevices and the base of the cliffs (vertex colour carries ambient occlusion)
	col *= mix(0.55, 1.0, COLOR.r);
	col *= 0.75 + 0.25 * g.a;
	// moss on tops
	float moss = smoothstep(0.6, 0.95, up) * smoothstep(0.45, 0.7, fbm2(wpos.xz * 0.5)) * (0.5 + wetness * 0.5);
	col = mix(col, season_foliage(vec3(0.20, 0.27, 0.10), 0.4), moss * 0.7);
	// snow
	float sn = snow_cover * smoothstep(0.45, 0.8, up + fbm2(wpos.xz * 0.6) * 0.3);
	col = mix(col, vec3(0.90, 0.92, 0.96), sn);
	col *= cloud_shadow(wpos.xz);
	col *= 1.0 - wetness * 0.25;
	ALBEDO = col;
	ROUGHNESS = mix(0.82, 0.55, wetness) ;
	SPECULAR = 0.4;
}
