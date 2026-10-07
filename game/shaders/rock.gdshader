// Granite massifs, painted in the manner of old isometric strategy games (8th Wonder of the World): dark charcoal
// rock broken into lumpy, faceted boulders (cellular noise bumps the normal, flat facets catch the light), pale lit
// tops and edges, deep crevices between the blocks, strata on cliff faces, moss and snow settling on top.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;

varying vec3 wpos;
varying vec3 wnorm;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec4 tri(vec3 p, vec3 n, float s) {
	vec3 b = pow(abs(n), vec3(4.0));
	b /= b.x + b.y + b.z;
	return texture(noise_tex, p.zy * s) * b.x + texture(noise_tex, p.xz * s) * b.y + texture(noise_tex, p.xy * s) * b.z;
}

void fragment() {
	vec3 n = normalize(wnorm);
	// facets: the triangle's own flat normal mixed in, so faces read as chiselled planes
	vec3 flat_n = normalize(cross(dFdx(wpos), dFdy(wpos)));
	if (dot(flat_n, n) < 0.0) flat_n = -flat_n;
	n = normalize(mix(n, flat_n, 0.55));
	// boulders: bump the normal along the gradient of the cellular noise
	float e = 0.06;
	float c0 = tri(wpos, n, 0.42).g;
	float cx = tri(wpos + vec3(e, 0, 0), n, 0.42).g, cy = tri(wpos + vec3(0, e, 0), n, 0.42).g, cz = tri(wpos + vec3(0, 0, e), n, 0.42).g;
	vec3 grad = vec3(cx - c0, cy - c0, cz - c0) / e;
	n = normalize(n - (grad - n * dot(grad, n)) * 0.18);
	vec4 big = tri(wpos, n, 0.09), fine = tri(wpos, n, 0.9);

	float up = clamp(n.y, 0.0, 1.0);
	vec3 dark = vec3(0.17, 0.17, 0.19), mid = vec3(0.33, 0.32, 0.32), light = vec3(0.62, 0.61, 0.58);
	vec3 col = mix(dark, mid, smoothstep(0.25, 0.75, big.r * 0.7 + fine.b * 0.3));
	// pale lit tops and boulder crowns, deep cracks between the blocks
	col = mix(col, light, smoothstep(0.55, 0.95, up) * (0.45 + 0.55 * (1.0 - c0)) * 0.75);
	col *= mix(1.0, 0.45, smoothstep(0.62, 0.82, c0));
	// strata bands on steep faces
	float strata = sin(wpos.y * 3.1 + big.a * 6.0) * 0.5 + 0.5;
	col *= mix(1.0, 0.8 + strata * 0.3, 1.0 - up);
	// weathering darkens crevices and the base of the cliffs (vertex colour carries ambient occlusion)
	col *= mix(0.5, 1.0, COLOR.r);
	// moss on tops
	float moss = smoothstep(0.6, 0.95, up) * smoothstep(0.55, 0.75, big.a) * (0.5 + wetness * 0.5);
	col = mix(col, vec3(0.24, 0.36, 0.10), moss * 0.6);
	// snow
	float sn = snow_cover * smoothstep(0.45, 0.8, up + fine.r * 0.3);
	col = mix(col, vec3(0.80, 0.83, 0.89), sn);
	col *= cloud_shadow(wpos.xz) * 0.6 + 0.4;
	col *= 1.0 - wetness * 0.25;
	ALBEDO = pow(col, vec3(2.2));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
	ROUGHNESS = mix(0.85, 0.5, wetness);
	SPECULAR = 0.35;
}
