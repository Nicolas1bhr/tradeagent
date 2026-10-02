from statistics import NormalDist
import math
Z=NormalDist(); g=0.5772156649; q=365
def emax(N): return (1-g)*Z.inv_cdf(1-1/N)+g*Z.inv_cdf(1-1/(N*math.e))
def dsr(sr_ann, years, N, g3=0, g4=3):
    T=int(years*q); sr=sr_ann/math.sqrt(q)
    sr0=emax(N)/math.sqrt(T) if N>1 else 0.0  # per-period SR0 with V[SR_n]=1/T under H0
    return Z.cdf((sr-sr0)*math.sqrt(T-1)/math.sqrt(1-g3*sr+(g4-1)/4*sr*sr)), sr0*math.sqrt(q)
for yrs in (0.75,1,2,3,5,8):
    row=[]
    for N in (20,50,200):
        for S in (1.5,2.0,3.0):
            d,s0=dsr(S,yrs,N)
            row.append(f"N{N}/SR{S}:{d:.2f}")
    print(f"years={yrs}: SR0(N=20,50,200)=" + ",".join(f"{emax(N)/math.sqrt(yrs):.2f}" for N in (20,50,200)) + " | " + " ".join(row))
print()
print("Approx years for betting e-process to reach ln(1/a) at optimal growth SR^2/2 per year:")
for inv in (20,100,200,1000):
    print(f" 1/alpha={inv} ({math.log(inv):.1f} nats): " + ", ".join(f"SR{S}:{2*math.log(inv)/S**2:.2f}y" for S in (1.0,1.5,2.0,3.0)))
# LORD default gamma first terms and e-LOND thresholds
C=0.07720838
gam=[C*math.log(max(j,2))/(j*math.exp(math.sqrt(math.log(j)))) for j in range(1,11)]
print("gamma1..5:",[round(x,4) for x in gam[:5]])
for a in (0.05,0.1):
    print(f"alpha={a}: e-LOND first-test threshold (R=0) = {1/(a*gam[0]):.0f}; 5th test R=0: {1/(a*gam[4]):.0f}")
# pooled k uncorrelated strategies
for k in (1,4,9):
    print(f"k={k} uncorrelated SR1.0 -> pooled SR {math.sqrt(k):.2f}, MinTRL vs0 normal {(1.645/math.sqrt(k))**2*(1+0.5*(math.sqrt(k)/math.sqrt(q))**2):.2f}y")
# CUSUM detection delay of edge vanishing: KL per day = SRd^2/2
for S in (1.5,2.0,3.0):
    sd=S/math.sqrt(q); kl=sd*sd/2
    print(f"SR{S}: KL/day={kl:.4f}; days to accumulate h=ln(1000)={math.log(1000)/kl:.0f}")
# cost drag
for rt_per_year,lab in ((365,'1 RT/day'),(52,'1 RT/week'),(12,'1 RT/month')):
    for c in (0.0020,0.0015,0.0010):
        print(f"{lab} at {c*100:.2f}% RT: {rt_per_year*c*100:.1f}%/yr")
