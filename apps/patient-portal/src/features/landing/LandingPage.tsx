import {
  AUTH_ERROR_PARAM,
  Alert,
  Button,
  Card,
  CardDescription,
  CardTitle,
  googleSignInUrl,
  useApiCodeMessage,
  useSession,
} from '@clinic/shared';
import { useTranslation } from 'react-i18next';
import { Navigate, useSearchParams } from 'react-router';

/**
 * P1 — Landing / sign-in (docs/06-ui-surfaces.md).
 *
 * The public entry point, and the surface a recruiter sees first (Z2), so it is the one
 * screen in this change that gets real visual attention. Everything on it is here for a
 * reason: what the clinic does, why the slots you will see are trustworthy, one way in, and
 * a language switch.
 *
 * The booking flow it promises arrives in change 5 — this change delivers the door, not the
 * building behind it.
 */
export function LandingPage() {
  const { t } = useTranslation();
  const { data: session, isPending } = useSession();
  const [searchParams] = useSearchParams();
  const describeCode = useApiCodeMessage();

  // A failed Google sign-in comes back as a redirect carrying the code, because the flow is
  // a top-level navigation and a JSON body in the address bar is not an error message
  // (design A5). Translating it here is what makes that decision pay off.
  const signInError = describeCode(searchParams.get(AUTH_ERROR_PARAM));

  if (session && !isPending) {
    // Booking, not the profile, from `booking-core` on. P1's stated purpose is to explain the
    // clinic and START BOOKING (06 §P1); sending a signed-in patient to their own record was only
    // ever right while booking did not exist.
    return <Navigate to="/book" replace />;
  }

  return (
    <div className="mx-auto grid max-w-5xl gap-10 px-6 py-16 md:grid-cols-2 md:items-center md:py-24">
      <section className="space-y-6">
        <p className="text-sm font-semibold tracking-wide text-primary uppercase">
          {t('portal.clinicName')}
        </p>

        <h1 className="text-4xl leading-tight font-semibold text-heading md:text-5xl">
          {t('portal.tagline')}
        </h1>

        <p className="max-w-prose text-lg text-meta">{t('portal.valueLine')}</p>
      </section>

      <Card className="space-y-5 bg-surface p-6 shadow-float md:p-8">
        <div className="space-y-1">
          <CardTitle>{t('common.signIn')}</CardTitle>
          <CardDescription>{t('portal.signInNote')}</CardDescription>
        </div>

        {signInError ? <Alert tone="error">{signInError}</Alert> : null}

        {/*
          A link, not a fetch: the flow leaves this origin for Google's consent screen and
          comes back with a cookie, which an XHR cannot follow. `asChild` keeps it an anchor
          so middle-click and open-in-new-tab still behave.
        */}
        <Button asChild size="lg" className="w-full">
          <a href={googleSignInUrl('/book')}>{t('portal.signInWithGoogle')}</a>
        </Button>
      </Card>
    </div>
  );
}
