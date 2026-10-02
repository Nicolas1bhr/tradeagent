from statistics import NormalDist
import math
Z=NormalDist()
g=0.5772156649
def emax(N):
    return (1-g)*Z.inv_cdf(1-1/N)+g*Z.inv_cdf(1-1/(N*math.e))
q=365
print("Expected max annualized SR of N zero-skill strategies, window of D days (Var[SR_ann] ~ q/D under H0, IID normal):")
for D in (91,182,273,365,730):
    row=[]
    for N in (10,50,200,1000,10000):
        row.append(f"N={N}:{emax(N)*math.sqrt(q/D):.2f}")
    print(f" D={D:4d}d  "+"  ".join(row))
print()
print("MinTRL (years, 365 obs/yr, one-sided 95%) for observed annual SR vs benchmark SR*")
z=Z.inv_cdf(0.95)
for (g3,g4,lab) in ((0,3,'normal'),(-0.5,6,'skew-0.5,kurt6'),(-1,12,'skew-1,kurt12')):
    print(' moments:',lab)
    for SRa in (1.0,1.5,2.0,3.0):
        out=[]
        for SRs in (0.0,0.5,1.0):
            if SRa<=SRs: out.append(f"SR*={SRs}: n/a"); continue
            sr=SRa/math.sqrt(q); srs=SRs/math.sqrt(q)
            n=1+(1-g3*sr+(g4-1)/4*sr*sr)*(z/(sr-srs))**2
            out.append(f"SR*={SRs}: {n/q:.2f}y ({n:.0f}d)")
        print(f"  SR_obs={SRa}: "+"; ".join(out))
print()
# DSR example: holdout of D days, candidate SR, with N effective trials and V[SR] from H0
print("PSR/DSR examples (daily obs):")
def psr(sr_ann, sr0_ann, T, g3=0, g4=3):
    sr=sr_ann/math.sqrt(q); s0=sr0_ann/math.sqrt(q)
    return Z.cdf((sr-s0)*math.sqrt(T-1)/math.sqrt(1-g3*sr+(g4-1)/4*sr*sr))
for D in (91,365):
    for N in (1,10,50,200):
        sr0 = 0 if N==1 else emax(N)*math.sqrt(q/D)
        for SRa in (2.0,3.0,4.0):
            print(f" D={D} N={N} SR0_ann={sr0:.2f} SR_obs={SRa} PSR={psr(SRa,0,D):.3f} DSR={psr(SRa,sr0,D):.3f}")
print()
# RCK fraction of Kelly in quadratic regime: f <= 2/(lambda+1) * Kelly
for a,b in ((0.7,0.1),(0.8,0.1),(0.8,0.05),(0.9,0.1),(0.85,0.05)):
    lam=math.log(b)/math.log(a)
    print(f"alpha={a} beta={b} lambda={lam:.2f} kelly_fraction<= {2/(lam+1):.3f}")
# implied independent trials N = rho + (1-rho) M
for M in (200,1000,5000):
    print(M,[round(r+(1-r)*M) for r in (0.2,0.5,0.8,0.9)])
