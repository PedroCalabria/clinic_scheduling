import {
  Alert,
  ApiRequestError,
  Button,
  Field,
  Input,
  bookAppointment,
  getBookingOptions,
  getMyProfile,
  grantConsent,
  updateMyProfile,
  useApiErrorMessage,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { TAKEN_CODE_PARAM, TAKEN_SLOT_PARAM } from './BookingSearchPage';
import { slotTime } from './slots';

/** The consent booking requires. Named once here, matching `ConsentType` on the server. */
const DATA_PROCESSING = 'DataProcessing';

/**
 * The refusals that mean "this slot is gone, go back and pick another".
 *
 * Everything else — a consent that needs granting, a phone that needs typing — is recoverable on
 * this screen, so it is shown here instead. Getting this list wrong in either direction is a real
 * UX failure: too wide and a patient is bounced out for something they could have fixed; too narrow
 * and they sit on a confirm button that will never work.
 */
const SLOT_GONE = new Set([
  'booking.slot_taken',
  'booking.slot_blocked',
  'booking.patient_busy',
  'booking.resource_unavailable',
  'booking.outside_working_hours',
  'booking.lead_time_violation',
  'booking.horizon_exceeded',
  'booking.specialty_mismatch',
]);

/**
 * P3 — slot select and confirm (docs/06-ui-surfaces.md, design B12, B15).
 *
 * Three jobs, and the second is not the one the screen inventory predicted:
 *
 * 1. **Show what is about to be booked** — professional, kind of visit, time in clinic wall clock.
 * 2. **Hold the consent gate.** `06 §P3` describes capturing data-processing consent for a
 *    first-time patient, but change 2 already grants it at Google sign-in, so there is nothing to
 *    capture. What this screen actually owns is the *gate*: booking now requires an active consent
 *    at the current version, which closes a loop change 2 opened by making revocation possible on
 *    P7 with nothing checking it. So the consent is shown, and offered again where it is missing.
 * 3. **Collect the one thing provisioning cannot know** — a contact phone. Google supplies a name
 *    and an email; nothing supplies a phone. Nothing else is asked for, because LGPD minimisation
 *    is a stated principle and a confirm screen is exactly where a form grows a birth date it does
 *    not need.
 */
export function ConfirmBookingPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();
  const [params] = useSearchParams();

  const appointmentTypeId = params.get('type') ?? '';
  const professionalId = params.get('professional') ?? '';
  const start = params.get('start') ?? '';
  const end = params.get('end') ?? '';
  const search = params.get('search') ?? '';

  const backToSearch = search ? `/book?${search}` : '/book';

  const profile = useQuery({ queryKey: ['my-profile'], queryFn: getMyProfile, retry: false });
  const options = useQuery({ queryKey: ['booking-options'], queryFn: getBookingOptions, retry: false });

  const appointmentType = options.data?.specialties
    .flatMap((specialty) => specialty.appointmentTypes)
    .find((type) => type.appointmentTypeId === appointmentTypeId);

  const professional = appointmentType?.professionals.find(
    (entry) => entry.professionalId === professionalId,
  );

  const consent = profile.data?.consents.find(
    (entry) => entry.type === DATA_PROCESSING && entry.active,
  );

  const timezone = options.data?.timezone;

  const [phone, setPhone] = useState('');

  // Seeded from the record once it arrives, rather than initialised from it: the query resolves
  // after first render, and a controlled input initialised from `undefined` would ignore the value
  // when it turns up.
  useEffect(() => {
    if (profile.data?.contactPhone) {
      setPhone(profile.data.contactPhone);
    }
  }, [profile.data?.contactPhone]);

  const needsPhone = profile.isSuccess && !profile.data.contactPhone;

  const grant = useMutation({
    mutationFn: () => grantConsent(DATA_PROCESSING),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['my-profile'] }),
  });

  const book = useMutation({
    mutationFn: async () => {
      // The phone first, and only when it is missing: a booking that succeeded while the phone
      // silently failed would leave the clinic unable to reach the patient about the appointment
      // they just made.
      if (needsPhone && phone.trim()) {
        await updateMyProfile(profile.data!.fullName, phone.trim());
        await queryClient.invalidateQueries({ queryKey: ['my-profile'] });
      }

      return bookAppointment({
        appointmentTypeId,
        professionalId,

        // The instant exactly as the availability response gave it. Not re-derived, not
        // re-formatted, and never a wall-clock label (Q4) — and deliberately no resource id, which
        // the request contract does not even carry.
        startsAt: start,
      });
    },
    onSuccess: (appointment) => {
      const done = new URLSearchParams({
        id: appointment.id,
        start: appointment.startsAt,
        end: appointment.endsAt,
        timezone: appointment.timezone,
        type: appointmentTypeId,
        professional: professionalId,
      });

      void navigate(`/book/success?${done.toString()}`, { replace: true });
    },
    onError: (error) => {
      const code = error instanceof ApiRequestError ? error.error.code : undefined;

      if (code && SLOT_GONE.has(code)) {
        // Back to P2 with the reason and the offending slot, so the search a patient made is still
        // on screen and the slot that failed is no longer offered (design B14).
        const back = new URLSearchParams(search);

        back.set(TAKEN_CODE_PARAM, code);
        back.set(TAKEN_SLOT_PARAM, start);

        void navigate(`/book?${back.toString()}`, { replace: true });
      }
    },
  });

  if (!start || !appointmentTypeId || !professionalId) {
    // A deep link with nothing to confirm. Reported rather than crashed, because P3's URL is
    // shareable and somebody will trim it.
    return (
      <div className="space-y-4">
        <Alert tone="error">{t('booking.confirmMissing')}</Alert>
        <Button onClick={() => void navigate('/book')}>{t('booking.backToSearch')}</Button>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      <header className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight text-heading">{t('booking.confirmTitle')}</h1>
        <p className="text-meta">{t('booking.confirmDescription')}</p>
      </header>

      <SlotSummary
        appointmentTypeName={appointmentType?.name}
        professionalName={professional?.displayName}
        start={start}
        end={end}
        timezone={timezone}
        language={i18n.language}
      />

      {profile.isError ? <Alert tone="error">{describeError(profile.error)}</Alert> : null}

      {/*
        The consent, shown whatever its state. A patient who has never thought about it sees that it
        is in place; one who withdrew it on P7 sees why they cannot continue and can fix it here
        rather than being sent to another screen and back.
      */}
      {profile.isSuccess ? (
        consent ? (
          <p className="text-sm text-meta">
            {t('booking.consentActive', { version: consent.version })}
          </p>
        ) : (
          <Alert tone="warning">
            <div className="space-y-3">
              <p>{t('booking.consentRequired')}</p>
              <Button size="sm" onClick={() => grant.mutate()} disabled={grant.isPending}>
                {t('booking.grantConsent')}
              </Button>
              {grant.isError ? <p className="text-sm">{describeError(grant.error)}</p> : null}
            </div>
          </Alert>
        )
      ) : null}

      <form
        className="space-y-6"
        onSubmit={(event) => {
          event.preventDefault();
          book.mutate();
        }}
      >
        {needsPhone ? (
          <div className="space-y-4 rounded-xl border border-line bg-surface-raised p-6">
            <div>
              <h2 className="font-medium text-heading">{t('booking.detailsTitle')}</h2>
              {/* Says out loud that this is asked once and why — the minimisation promise, visible. */}
              <p className="mt-1 text-sm text-meta">{t('booking.detailsHint')}</p>
            </div>

            <p className="text-sm text-meta">
              {t('booking.nameOnRecord', { name: profile.data.fullName })}{' '}
              <Link to="/profile" className="underline">
                {t('booking.editOnProfile')}
              </Link>
            </p>

            <Field label={t('profile.contactPhone')}>
              {({ id, describedBy }) => (
                <Input
                  id={id}
                  type="tel"
                  autoComplete="tel"
                  aria-describedby={describedBy}
                  value={phone}
                  onChange={(event) => setPhone(event.target.value)}
                  required
                />
              )}
            </Field>
          </div>
        ) : null}

        {/*
          Only the refusals this screen can do something about reach here; the ones that mean the
          slot is gone have already navigated back to the search.
        */}
        {book.isError ? <Alert tone="error">{describeError(book.error)}</Alert> : null}

        <div className="flex flex-wrap gap-3">
          <Button type="submit" disabled={book.isPending || !consent}>
            {book.isPending ? t('booking.confirming') : t('booking.confirm')}
          </Button>

          <Button type="button" variant="secondary" onClick={() => void navigate(backToSearch)}>
            {t('booking.backToSearch')}
          </Button>
        </div>
      </form>
    </div>
  );
}

/**
 * What is about to be booked.
 *
 * The times come from the instants P2 handed over, rendered in the clinic's zone — which this
 * component has to be told, because a summary that quietly used the browser's zone would show a
 * different time from the one the patient clicked.
 */
function SlotSummary({
  appointmentTypeName,
  professionalName,
  start,
  end,
  timezone,
  language,
}: {
  appointmentTypeName?: string;
  professionalName?: string;
  start: string;
  end: string;
  /** The CLINIC's zone, from the options response — never the browser's. */
  timezone?: string;
  language: string;
}) {
  const { t } = useTranslation();

  return (
    <dl className="grid gap-4 rounded-xl border border-line bg-surface-raised p-6 sm:grid-cols-3">
      <div>
        <dt className="text-xs uppercase tracking-wide text-meta">{t('booking.appointmentType')}</dt>
        <dd className="mt-1 font-medium text-heading">{appointmentTypeName ?? '—'}</dd>
      </div>
      <div>
        <dt className="text-xs uppercase tracking-wide text-meta">{t('booking.professional')}</dt>
        <dd className="mt-1 font-medium text-heading">{professionalName ?? '—'}</dd>
      </div>
      <div>
        <dt className="text-xs uppercase tracking-wide text-meta">{t('booking.when')}</dt>
        <dd className="mt-1 font-mono font-medium tabular-nums text-heading">
          {timezone ? (
            <>
              {new Intl.DateTimeFormat(language, {
                timeZone: timezone,
                weekday: 'long',
                day: 'numeric',
                month: 'long',
              }).format(new Date(start))}{' '}
              {slotTime(start, timezone)}–{slotTime(end, timezone)}
            </>
          ) : (
            // Withheld rather than guessed: showing a time in the wrong zone for one render is
            // worse than showing none, because a patient would read it and believe it.
            '—'
          )}
        </dd>
      </div>
    </dl>
  );
}
