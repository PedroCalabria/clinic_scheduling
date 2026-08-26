import {
  Alert,
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogFooter,
  calendarConnectUrl,
  checkCalendarConnection,
  disconnectCalendar,
  getCalendarConnection,
  useApiCodeMessage,
  useApiErrorMessage,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';

export const CALENDAR_QUERY_KEY = ['calendar-connection'] as const;

/** Where the flow returns to, and the only screen that starts it. */
const RETURN_PATH = '/staff/calendar';

/**
 * S2 — a professional connects, checks and withdraws their own Google Calendar
 * (docs/06-ui-surfaces.md; change 6a).
 *
 * **What this screen must be honest about.** Nothing calls Google on a schedule in this change
 * — there is no scheduler until 6b — so the status here is the result of the last look, not
 * current truth. It is therefore always rendered together with *when* that look happened, and
 * with a control that asks again. A badge alone would be believed more than it deserves.
 *
 * **And about what connecting buys today: nothing yet.** No appointment reaches a calendar
 * until 6b's outbox ships. Saying so is better than a screen that implies a benefit it cannot
 * deliver — and it is the reason the never-connected state is presented as a state rather than
 * as a problem to fix.
 */
export function CalendarConnectionPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  // The flow reports refusals through a query parameter rather than a thrown error, because it
  // returns from Google by top-level navigation. `useApiCodeMessage` exists for exactly that.
  const describeCode = useApiCodeMessage();
  const [params, setParams] = useSearchParams();

  const [confirming, setConfirming] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  /**
   * The flow reports itself through the URL, because it comes back from Google by top-level
   * navigation — there is no fetch whose result could carry it.
   */
  const flowError = params.get('calendarError');
  const justConnected = params.get('calendarConnected') === '1';

  const { data, isPending, isError, error } = useQuery({
    queryKey: CALENDAR_QUERY_KEY,
    queryFn: getCalendarConnection,
    retry: false,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: CALENDAR_QUERY_KEY });

  const check = useMutation({
    mutationFn: checkCalendarConnection,
    onSuccess: () => {
      setNotice(t('calendar.checkedOk'));
      void invalidate();
    },
    // A revoked grant arrives here as an error, which is correct: the check found something
    // the professional has to act on, and the refusal carries the code that says what.
    onError: () => setNotice(null),
  });

  const disconnect = useMutation({
    mutationFn: disconnectCalendar,
    onSuccess: (result) => {
      // Two different sentences, and the difference is not cosmetic. Reporting plain success
      // when Google refused the revocation would tell somebody something untrue about their
      // own data (design K9).
      setNotice(
        result.revokedAtProvider
          ? t('calendar.disconnected')
          : t('calendar.disconnectedNotAtProvider'),
      );
      setConfirming(false);
      void invalidate();
    },
  });

  /** Clears whatever the redirect said, so a reload does not re-report a stale outcome. */
  function clearFlowResult() {
    if (flowError || justConnected) {
      setParams({}, { replace: true });
    }
  }

  const status = data?.status ?? 'NotConnected';

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-heading">{t('calendar.title')}</h1>
        <p className="mt-1 text-meta">{t('calendar.description')}</p>
      </div>

      {/*
        Said once, plainly, at the top: connecting changes nothing yet. A professional who
        connects expecting their appointments to appear would otherwise conclude the feature is
        broken — which it is not, it is unfinished, and those deserve different sentences.
      */}
      <Alert tone="info">{t('calendar.nothingSyncsYet')}</Alert>

      {justConnected ? <Alert tone="success">{t('calendar.connectedExplanation')}</Alert> : null}
      {flowError ? <Alert tone="error">{describeCode(flowError)}</Alert> : null}
      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {check.isError ? <Alert tone="error">{describeError(check.error)}</Alert> : null}
      {disconnect.isError ? <Alert tone="error">{describeError(disconnect.error)}</Alert> : null}

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : (
        <section className="space-y-4 rounded-sm border border-line bg-surface-raised p-5">
          <div className="flex flex-wrap items-center gap-3">
            <Badge tone={status === 'Connected' ? 'active' : 'neutral'}>
              {t(`calendar.status${status}`)}
            </Badge>

            {/*
              The observation moment, never separated from the status it qualifies. This pairing
              is the whole reason the response carries both.
            */}
            <span className="text-sm text-meta" data-testid="calendar-observed">
              {data?.stateObservedAtUtc
                ? t('calendar.lastChecked', { when: formatMoment(data.stateObservedAtUtc) })
                : t('calendar.neverChecked')}
            </span>
          </div>

          <p className="text-body">{t(`calendar.${explanationKey(status)}`)}</p>

          {data?.stateObservedAtUtc ? <p className="text-sm text-meta">{t('calendar.staleNote')}</p> : null}

          {data?.connectedAtUtc ? (
            <p className="text-sm text-meta">
              {t('calendar.connectedOn', { when: formatMoment(data.connectedAtUtc) })}
            </p>
          ) : null}

          {data?.targetCalendarId ? (
            <p className="text-sm text-meta">{t('calendar.calendar', { name: data.targetCalendarId })}</p>
          ) : null}

          {/*
            What they agreed to, and when. S2 is the only surface that can show this: consents
            are otherwise read through the patient profile, so a professional had no way to see
            their own (design K12).
          */}
          {data?.consentVersion && data.consentGrantedAtUtc ? (
            <p className="text-sm text-meta">
              {t('calendar.consentGranted', {
                when: formatMoment(data.consentGrantedAtUtc),
                version: data.consentVersion,
              })}
            </p>
          ) : null}

          <div className="flex flex-wrap gap-3 pt-1">
            {/*
              An anchor, not a button with a fetch. Connecting is a top-level navigation to
              Google and back — the state cookie has to be set on a request the browser follows,
              and an XHR would die on CORS at Google's consent screen.
            */}
            <Button asChild>
              <a href={calendarConnectUrl(RETURN_PATH)} onClick={clearFlowResult}>
                {connectLabel(status) === 'reconnect' ? t('calendar.reconnect') : t('calendar.connect')}
              </a>
            </Button>

            {data?.connected || status === 'Revoked' ? (
              <>
                <Button
                  variant="secondary"
                  onClick={() => {
                    setNotice(null);
                    check.mutate();
                  }}
                  disabled={check.isPending}
                >
                  {check.isPending ? t('calendar.checking') : t('calendar.check')}
                </Button>

                <Button
                  variant="secondary"
                  onClick={() => {
                    setNotice(null);
                    disconnect.reset();
                    setConfirming(true);
                  }}
                >
                  {t('calendar.disconnect')}
                </Button>
              </>
            ) : null}
          </div>
        </section>
      )}

      {/*
        The shared dialog primitive rather than a hand-rolled confirmation: focus trapping,
        focus restoration to the trigger, Escape-to-close and inert background content are a
        list this project would get subtly wrong by hand (00-context.md §2).
      */}
      <Dialog open={confirming} onOpenChange={(next) => !next && setConfirming(false)}>
        {confirming ? (
          <DialogContent title={t('calendar.disconnectTitle')}>
            <p className="text-body">{t('calendar.disconnectExplanation')}</p>

            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => setConfirming(false)}>
                {t('calendar.cancel')}
              </Button>
              <Button
                type="button"
                variant="danger"
                onClick={() => disconnect.mutate()}
                disabled={disconnect.isPending}
              >
                {t('calendar.disconnectConfirm')}
              </Button>
            </DialogFooter>
          </DialogContent>
        ) : null}
      </Dialog>
    </div>
  );
}

/** Which sentence belongs to which state. Four states, four explanations, no default prose. */
function explanationKey(status: string): string {
  switch (status) {
    case 'Connected':
      return 'connectedExplanation';
    case 'Revoked':
      return 'revokedExplanation';
    case 'Disconnected':
      return 'disconnectedExplanation';
    default:
      return 'notConnectedExplanation';
  }
}

/**
 * "Connect" for somebody who never had one, "Reconnect" for somebody restoring it.
 *
 * A small distinction that carries real information: reconnect tells a professional their
 * permission lapsed rather than implying they never granted it.
 */
function connectLabel(status: string): 'connect' | 'reconnect' {
  return status === 'Revoked' || status === 'Disconnected' ? 'reconnect' : 'connect';
}

/**
 * These are instants, and they are rendered in the reader's own locale and zone.
 *
 * Deliberately unlike every appointment time in this console, which is clinic wall clock
 * (Decision H): "when did I last check" and "when did I grant this" are facts about the
 * professional's own actions, not about the clinic's schedule, so their own clock is the
 * right one.
 */
function formatMoment(value: string): string {
  return new Date(value).toLocaleString();
}
