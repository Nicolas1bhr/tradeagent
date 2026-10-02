# Red-team arithmetic for EDGE-FACTORY §4.5-4.6 (IID normal daily returns, 365 obs/yr; same assumptions as R04-calc).
from statistics import NormalDist
from math import comb, sqrt, log, e
Z = NormalDist(); g = 0.5772156649; q = 365
def emax(N): return (1-g)*Z.inv_cdf(1-1/N) + g*Z.inv_cdf(1-1/(N*e))

print("E2 block test: P(>=6 of 8 sub-periods positive | true annual SR), window in days")
for D in (139, 273, 365, 1825):
    row = []
    for S in (0.0, 0.5, 1.0, 1.5, 2.0):
        p = Z.cdf(S*sqrt(D/8/q))
        P = sum(comb(8,k)*p**k*(1-p)**(8-k) for k in (6,7,8))
        row.append(f"SR{S}:{P:.2f}")
    print(f"  D={D:5d}  " + "  ".join(row))

print("\nE3: observed annual SR needed for DSR>=0.95 (N_eff trials, normal moments), and P(pass | true SR)")
for D in (139, 273, 365, 730, 1825):
    T = D
    for N in (20, 50, 200):
        sr0 = emax(N)*sqrt(q/D)          # annualised noise ceiling
        thr = sr0 + 1.645*sqrt(q/(T-1))  # approx: need (SR-SR0)*sqrt(T-1)/sqrt(q) >= 1.645
        se = sqrt(q/T)
        ps = [1-Z.cdf((thr-S)/se) for S in (1.0, 1.5, 2.0)]
        print(f"  D={D:5d} N={N:3d} ceiling={sr0:.2f} need SR_obs>={thr:.2f}  P(pass|SR1,1.5,2)={ps[0]:.2f},{ps[1]:.2f},{ps[2]:.2f}")

print("\nE4 with a 91-day holdout: z>=-2 tolerance in annual SR units = 2*se =", round(2*sqrt(q/91),2),
      "; P(net>0 | SR 0) ~ 0.50 (before costs)")

print("\nE7 e-LOND, alpha_live=0.10, gamma_j=1/K, K=10 -> first test needs E >= 1/(0.1*0.1) = 100")
for S in (1.0, 1.5, 2.0):
    full = log(100)/(S*S/2); half = log(100)/(3*S*S/8)
    print(f"  SR{S}: years to E>=100  full-Kelly growth {full:.1f} y | half-Kelly growth {half:.1f} y")

print("\nE6 L1: forward annual SR needed for P(SR>0)>=0.80, prior N(m0, 0.5^2), likelihood se^2 = 1/years")
for m0 in (0.0, 0.3):
    row = []
    for yrs in (0.25, 0.5, 1.0, 2.0):
        prec = 1/0.25 + yrs; var = 1/prec; sd = sqrt(var)
        # mean = (m0*4 + SRhat*yrs)/prec ; need mean >= 0.8416*sd
        need = (0.8416*sd*prec - m0*4)/yrs
        row.append(f"{yrs}y:{need:.2f}")
    print(f"  prior mean {m0}: " + "  ".join(row))

print("\nPooling: k streams with pairwise correlation rho, each SR 1 -> pooled SR")
for rho in (0.0, 0.3, 0.7, 0.9):
    print(f"  rho={rho}: " + "  ".join(f"k{k}:{sqrt(k/(1+(k-1)*rho)):.2f}" for k in (4, 9)))

print("\nLearning tier economics: L1 capital = 5% of account; at SR 2 and 25% vol -> 50%/yr on the L1 slice")
for cap in (10_000, 100_000):
    l1 = 0.05*cap; earn = 0.5*l1
    print(f"  capital {cap}: L1 slice {l1:.0f}, gross earn/yr {earn:.0f} vs operating 210/month = 2520/yr")
