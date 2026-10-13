// Signatures of confirmed advertisements, sampled at 12 × 12 RGB pixels.
// Match the image content, not a chapter ID or a filename such as 0.jpg.
const banners = [
  { ratio: 506 / 900, sample: '/VjM8mHI5nHE5G7C5HbE5HHC5W/D4XPB6WvE5HHD82PI/VjM/1nN92DK7GrF5m/D4HjC43K95mu543TD6WrE7G3G92DK/1nN03K+/VrM+ljK026335XJ34eq3WZ94InH9HjP/1nN/lnN+ljJg11+yl+5sUOYUyVZ8lXD9lnFoGOO8FjE/1nN/1nNsEeUnUOFSFxdmVeJiDqIsmOg81fE7VfCgG12vGeT/1nN/1nNmUl81mWtMUA6dVlvhTSTbSl6h0h0jUt/cmlRv6Nc32++xVqldjRDjF1wcZaDo6CaTD1jiC2Hq2+JZVFTU01TlYB5vLK8poKMznBoiVpld5aPeX17p6PLq4q5gUdtcFpZU0VaVEA+qoeawIqoy5+PeFhRmKClu3uvlbXYnaDLYitOazJbnls9Xjw0pJCIvqytqIV60Jp6yJ7DoFGAeoihkoqXSyIwjQ4fpTdDYj1Tqay8lZSYXT9CzIxr59Xk1b+65NrX49jV07+/yJ+l2rLIva662NDa2dfVybSz6NfP6enp3Nzd2NjY2trb2NnZ6uHZ2tra19fX2tra2NjY2NjY6urq' },
  { ratio: 491 / 900, sample: 'HDdcV1trX15vQ0JdPj5cODtcNDhaMDJVMC5RMjJUOSpGQClISkNVwJeEzK2eineFcW2EcHuTm6Opd3qIR0xqPzJLYkRIQypGTTpNv3BMzH9Tn3dte19lv6WG1MScua6S0LeRhmhcdVRRQSI8VExa1KmHzaiJ1baTpoZ5xamEzr6b0cCgy66Ntph1fFtLWD5OXEBK34ZQ4bOO8eXKpoqExqyL1s+20Mu66OHGwp93iGl7Z0JGWkdQtU4qk1E+9e3Uz6+Tz7OS3Nm/3NzG5+PIwp54cU9db0pKWz9Qb0VBnHFa9e7W27ya3b2QyMClwL2rwrumw6F8kmBLXz5Lh2Vdh1pIk2tZ9O3V1cGf2LeK1reK3MKZ17SIupVziWhpZ0lQiG5mhGFWj2FL7eLHwK6TyLWitI1pxKOJxaSIoXpmfVpfX0FMa2Bte2dhh2JStYdit4lfupZxeldHjGxprY2ImG5jbUE7YURQSUFQaUZCeExDt4ZYhFhEtIldjF9JiF1Nv52MrIZ8hV9RakhYIDFPKio/LCY7LyY9MSU6PCo9Pys9PSQ6VzpNbEteVS1EVylI' },
  { ratio: 491 / 900, sample: 'VzZNtp2krpKXhm6EdWaDYFyBWFh9T092S0l0xMDDi4GRQShAelFf0q+h59TKkn2Pm4OAmoR/nYZ8inRwWmGJvq28bEtbWDxUelFf17WlyaCPt4l7pY+Yh7PTjLXUbWaEbllwop+wWjFFSilBw7Gs3cS12qB/63w0qI+YoLzQqsLSwK+ycVlrel5qemFvfmxurqGm4NDG6KZ+2YNP0LOY9enS29nL4d3N5dvI8da29d2/8+TLlXR6zrGd0J1/376i2sOuuqughZ6wna64q66t5K6C57aN8dS0d0pczZBwvYdw7bGCwH5b5c6zzsSyzsKw2s25yMe8vcfI99++tYRz3Gw713RB4ZZm3cSw7d7E2M2+zsCy1r2pzMK5vr+/9uXLvI+DsWBM3WErplg+nVxKxq2f18u4zsSzz8Sz0ce1ysCv++3TnoKUjWtyonJuil1Xr5aQzLen7eDIyb+tvLOjwbin6dzF9ebOm3+NqJCPoIyOyruwrJaTxbSp5dnC5NjD6NzF59vEwbWx4NPD5tXG8+TN79/Iybyv7ODK49nE39XA29K+4NbB4dfBv6KT07+t' },
  { ratio: 506 / 900, sample: 'W9j7Y9Pxc83kcMvjeM3jc8vics3kdcnfbc7oc8zjZdPxWtj7Wtr/Ydb3a9DrcM3mecvgd8jcdMbZdczjbM7obtDsYdX3Wtr/c73aW9r+Wdb6b73Ulc/fnbu+l52Ris7gedn0Wtr/Wtn+Wdb6UoWNYrLXSZW3Si1cVs/yXtDzcoecWc7xWtr/Wtr/SJmzRYifOWliSJehS2qahYatWNDzWsvufHB3a6eqWtr/Wtr/WHyParrNL0I7UnR4fjmVYjJ9V26CUXqRcmlQqLVjccbfX6vEbTtGdHB3cZaDmKmdQUVmfTWKg46VYlNUU01TlYB5s7m/nIqPznBoemZpcpqQcIR9nKvOlJzAUWx7a11aUkVaVEA+kZuhmqizy5+PclxSmKClbbnGlLXYi6/QQEdYOllqnls9Xjw0pJCIuq6uqIV6z5t7n7/QXIeUeIqikoqXSyIwjQ4fj0hKSFFbnbbAlZSYXT9CzIxr1eTp0sG64d3Y39vW07+/yJ+lvsnQrrm+zdnd2dbVybSz6NfP6enp3Nzc2NjY2tra2NjY6uHZ2tra19fX2tra2NjY2NjY6erq' }
].map(banner => ({ ...banner, pixels: Uint8Array.from(atob(banner.sample), c => c.charCodeAt(0)) }));

export function readerBannerCrop(width: number, height: number,
  sample: (top: number, height: number) => Uint8ClampedArray): number {
  return readerBannerBounds(width, height, sample).top;
}

export function readerBannerBounds(width: number, height: number,
  sample: (top: number, height: number) => Uint8ClampedArray): { top: number; bottom: number } {
  let top = 0;
  let bottom = height;
  // Trim confirmed banners at either edge, stopping at unrecognized content.
  for (const edge of ['top', 'bottom']) {
    for (let count = 0; count < 3; count++) {
      let matched = false;
      for (const banner of banners) {
        const bandHeight = Math.round(width * banner.ratio);
        if (bottom - top < bandHeight + 100) continue;
        const actual = sample(edge === 'top' ? top : bottom - bandHeight, bandHeight);
        let error = 0;
        for (let pixel = 0; pixel < 144; pixel++) {
          for (let channel = 0; channel < 3; channel++) {
            error += Math.abs(actual[pixel * 4 + channel] - banner.pixels[pixel * 3 + channel]);
          }
        }
        // Allow JPEG compression/resizing differences, but reject unrelated art.
        if (error / 432 > 18) continue;
        if (edge === 'top') top += bandHeight;
        else bottom -= bandHeight;
        matched = true;
        break;
      }
      if (!matched) break;
    }
  }
  return { top, bottom };
}
