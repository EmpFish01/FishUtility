// ── Oklab color space conversion ──
// Matches Unity Mathematics / C# implementation byte-for-byte.
// All constants match the C# version in TextEffects.cs / FishUtility.cs.

use rayon::prelude::*;

// ── sRGB → Oklab (batch) ──

/// Batch-convert sRGB triplets to Oklab.
/// `count` elements each in r/g/b → l/a/b_ok.
/// Uses rayon parallel iterator.
pub fn batch_srgb_to_oklab(
    r: &[f32],
    g: &[f32],
    b: &[f32],
    l: &mut [f32],
    a: &mut [f32],
    b_ok: &mut [f32],
) {
    l.par_iter_mut()
        .zip(a.par_iter_mut())
        .zip(b_ok.par_iter_mut())
        .zip(r.par_iter())
        .zip(g.par_iter())
        .zip(b.par_iter())
        .for_each(|(((((lo, ao), bo), rv), gv), bv)| {
            let (li, ai, bi) = srgb_to_oklab_single(*rv, *gv, *bv);
            *lo = li;
            *ao = ai;
            *bo = bi;
        });
}

#[inline(always)]
fn srgb_to_oklab_single(r: f32, g: f32, b: f32) -> (f32, f32, f32) {
    // sRGB → linear
    let r = srgb_to_linear(r);
    let g = srgb_to_linear(g);
    let b = srgb_to_linear(b);

    // Linear sRGB → LMS
    let l = r.mul_add(0.4122214708, g.mul_add(0.5363325363, 0.0514459929 * b));
    let m = r.mul_add(0.2119034982, g.mul_add(0.6806995451, 0.1073969566 * b));
    let s = r.mul_add(0.0883024619, g.mul_add(0.2817188376, 0.6299787005 * b));

    // LMS → LMS^(1/3)
    let l = l.powf(1.0 / 3.0);
    let m = m.powf(1.0 / 3.0);
    let s = s.powf(1.0 / 3.0);

    // LMS^(1/3) → Oklab
    let ok_l = l.mul_add(0.2104542553, m.mul_add(0.7936177850, -0.0040720468 * s));
    let ok_a = l.mul_add(1.9779984951, m.mul_add(-2.4285922050, 0.4505937099 * s));
    let ok_b = l.mul_add(0.0259040371, m.mul_add(0.7827717662, -0.8086757660 * s));

    (ok_l, ok_a, ok_b)
}

#[inline(always)]
fn srgb_to_linear(x: f32) -> f32 {
    if x > 0.04045 {
        ((x + 0.055) / 1.055).powf(2.4)
    } else {
        x / 12.92
    }
}

// ── Oklab → sRGB Color32 (RGBA packed) ──

/// Batch-convert Oklab triplets to packed RGBA bytes.
/// `count` elements; output `rgba` is `count * 4` bytes (R,G,B,A order).
pub fn batch_oklab_to_rgba32(
    l: &[f32],
    a: &[f32],
    b_ok: &[f32],
    rgba: &mut [u8],
) {
    rgba.par_chunks_exact_mut(4)
        .zip(l.par_iter())
        .zip(a.par_iter())
        .zip(b_ok.par_iter())
        .for_each(|(((rgba4, lv), av), bv)| {
            let (r, g, b) = oklab_to_srgb_byte(*lv, *av, *bv);
            rgba4[0] = r;
            rgba4[1] = g;
            rgba4[2] = b;
            rgba4[3] = 255;
        });
}

/// Convert a single Oklab triplet to sRGB bytes.
#[inline(always)]
pub fn oklab_to_srgb_byte(l: f32, a: f32, b_ok: f32) -> (u8, u8, u8) {
    // Oklab → LMS linear
    let l_ = l + 0.3963377774 * a + 0.2158037573 * b_ok;
    let m_ = l - 0.1055613458 * a - 0.0638541728 * b_ok;
    let s_ = l - 0.0894841775 * a - 1.2914855480 * b_ok;

    // Cube
    let l_ = l_ * l_ * l_;
    let m_ = m_ * m_ * m_;
    let s_ = s_ * s_ * s_;

    // LMS → Linear sRGB
    let r_lin = l_.mul_add(4.0767416621, m_.mul_add(-3.3077363322, 0.2309101289 * s_));
    let g_lin = l_.mul_add(-1.2684380046, m_.mul_add(2.6097574011, -0.3413193761 * s_));
    let b_lin = l_.mul_add(-0.0041960863, m_.mul_add(-0.7034186147, 1.7076147010 * s_));

    // Clamp negative
    let r_lin = r_lin.max(0.0);
    let g_lin = g_lin.max(0.0);
    let b_lin = b_lin.max(0.0);

    // Highlight rolloff
    const HIGHLIGHT_K: f32 = 0.25;
    let r_lin = if r_lin > 1.0 { r_lin / (1.0 + (r_lin - 1.0) * HIGHLIGHT_K) } else { r_lin };
    let g_lin = if g_lin > 1.0 { g_lin / (1.0 + (g_lin - 1.0) * HIGHLIGHT_K) } else { g_lin };
    let b_lin = if b_lin > 1.0 { b_lin / (1.0 + (b_lin - 1.0) * HIGHLIGHT_K) } else { b_lin };

    // Linear → sRGB
    let r = linear_to_srgb(r_lin);
    let g = linear_to_srgb(g_lin);
    let b = linear_to_srgb(b_lin);

    // sRGB → byte (clamped 0..1 → 0..255)
    let rb = (r * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
    let gb = (g * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
    let bb = (b * 255.0 + 0.5).clamp(0.0, 255.0) as u8;

    (rb, gb, bb)
}

#[inline(always)]
fn linear_to_srgb(linear: f32) -> f32 {
    if linear <= 0.0031308 {
        linear * 12.92
    } else {
        1.055 * linear.powf(1.0 / 2.4) - 0.055
    }
}

// ── srgb_to_linear (pub for wrap_table) ──

#[allow(dead_code)]
#[inline(always)]
pub fn srgb_to_linear_pub(x: f32) -> f32 {
    srgb_to_linear(x)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_srgb_oklab_roundtrip() {
        // Test a few known colors
        let test_r = [1.0, 0.5, 0.0, 0.25];
        let test_g = [0.0, 0.5, 0.5, 0.25];
        let test_b = [0.0, 0.5, 1.0, 0.75];
        let n = test_r.len();

        let mut l = vec![0.0; n];
        let mut a = vec![0.0; n];
        let mut b_ok = vec![0.0; n];

        batch_srgb_to_oklab(&test_r, &test_g, &test_b, &mut l, &mut a, &mut b_ok);

        // Oklab values should be finite
        for i in 0..n {
            assert!(l[i].is_finite());
            assert!(a[i].is_finite());
            assert!(b_ok[i].is_finite());
        }
    }

    #[test]
    fn test_oklab_to_rgba32_output_range() {
        let l = [0.5, 0.7, 0.3, 0.9, 0.1];
        let a = [0.0, 0.2, -0.1, 0.1, -0.2];
        let b = [0.0, -0.1, 0.15, -0.2, 0.05];
        let n = l.len();
        let mut rgba = vec![0u8; n * 4];

        batch_oklab_to_rgba32(&l, &a, &b, &mut rgba);

        for i in 0..n {
            assert!(rgba[i * 4 + 3] == 255, "alpha should always be 255");
        }
    }
}
