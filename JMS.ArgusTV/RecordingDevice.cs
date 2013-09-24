using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;


namespace JMS.ArgusTV
{
    /// <summary>
    /// Ein einzelnes Aufzeichnungsgerät.
    /// </summary>
    public abstract class RecordingDevice : IDisposable
    {
        /// <summary>
        /// Das Ergebnis eines Auflösevorgangs eines Namens gegen die Senderdaten.
        /// </summary>
        public abstract class SourceResolveResult
        {
        }

        /// <summary>
        /// Der Name des Gerätes.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Die Priorität des Gerätes, kleinere Werte werden bevorzugt.
        /// </summary>
        public int Priority { get; private set; }

        /// <summary>
        /// Gesetzt, sobald das Gerät freigegeben wurde.
        /// </summary>
        private int m_isDisposed;

        /// <summary>
        /// Erstellt eine neue Simulation.
        /// </summary>
        /// <param name="name">Der Name des Gerätes.</param>
        /// <param name="priority">Die Priorität des Gerätes - kleiner Werte sind besser.</param>
        protected RecordingDevice( string name, int priority )
        {
            // Remember
            Priority = priority;
            Name = name;
        }

        /// <summary>
        /// Beendet die Nutzung dieses Gerätes endgültig.
        /// </summary>
        protected abstract void OnDispose();

        /// <summary>
        /// Beginnt eine neue Aufzeichnung.
        /// </summary>
        /// <param name="source">Der zu verwendende Sender.</param>
        /// <param name="path">Der volle Pfad zur Aufzeichnungsdatei.</param>
        /// <returns>Gesetzt, wenn ein Start möglich war.</returns>
        public abstract Guid? Start( SourceResolveResult source, string path );

        /// <summary>
        /// Ermittelt zu einem Text den Sender.
        /// </summary>
        /// <param name="channelIdentification">Die Identifikation des Senders.</param>
        /// <returns>Die Senderinformationen.</returns>
        public abstract SourceResolveResult Resolve( string channelIdentification );

        /// <summary>
        /// Prüft, ob Sender gemeinsam aufgezeichnet werden können.
        /// </summary>
        /// <param name="left">Die Daten eines Senders.</param>
        /// <param name="right">Die Daten eines anderen Senders.</param>
        /// <returns>Gesetzt, wenn eine gemeinsame Aufzeichnung möglich ist.</returns>
        public abstract bool CheckForCommonSourceGroups( SourceResolveResult left, SourceResolveResult right );

        /// <summary>
        /// Beendet eine Aufzeichnung.
        /// </summary>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        public abstract void Stop( Guid streamIdentifier );

        /// <summary>
        /// Beendet die Nutzung dieses Gerätes endgültig.
        /// </summary>
        public void Dispose()
        {
            // Process once
            if (Interlocked.Exchange( ref m_isDisposed, 1 ) == 0)
                OnDispose();
        }
    }

    /// <summary>
    /// Ein einzelnes Aufzeichnungsgerät.
    /// </summary>
    /// <typeparam name="TSourceType">Die Art, wie Quellen identifiziert werden.</typeparam>
    /// <typeparam name="TAsyncControlType">Die Art, wie asynchrone Vorgänge gesteuert werden.</typeparam>
    public abstract class RecordingDevice<TSourceType, TAsyncControlType> : RecordingDevice
    {
        /// <summary>
        /// Das Ergebnis eines Auflösevorgangs eines Namens gegen die Senderdaten.
        /// </summary>
        private new class SourceResolveResult : RecordingDevice.SourceResolveResult
        {
            /// <summary>
            /// Alle ermittelten Senderdaten.
            /// </summary>
            public TSourceType[] Sources { get; private set; }

            /// <summary>
            /// Erstellt ein neues Ergebnis.
            /// </summary>
            /// <param name="sources">Die zugehörigen Senderinformationen.</param>
            public SourceResolveResult( TSourceType[] sources )
            {
                // Remember
                Sources = sources;
            }
        }

        /// <summary>
        /// Die Daten zu einer einzelnen Aufzeichnung.
        /// </summary>
        private class StreamInformation
        {
            /// <summary>
            /// Die Informationen zur Quelle.
            /// </summary>
            public TSourceType Source { get; private set; }

            /// <summary>
            /// Der volle Pfad zur Aufzeichnungsdatei.
            /// </summary>
            public string RecordingPath { get; private set; }

            /// <summary>
            /// Erstellt eine neue Information.
            /// </summary>
            /// <param name="source">Die gewünschte Quelle.</param>
            /// <param name="path">Der volle Pfad zur Aufzeichnungsdatei.</param>
            public StreamInformation( TSourceType source, string path )
            {
                // Remember
                RecordingPath = path;
                Source = source;
            }
        }

        /// <summary>
        /// Ein Eintrag der Warteschlange.
        /// </summary>
        private abstract class QueueItem
        {
            /// <summary>
            /// Die Methoden zum Starten der Operation.
            /// </summary>
            private readonly Func<TAsyncControlType> m_starter;

            /// <summary>
            /// Gesetzt, sobald der Eintrag bearbeitet wird.
            /// </summary>
            private int m_running;

            /// <summary>
            /// Erstellt einen neuen Eintrag in der Warteschlange.
            /// </summary>
            /// <param name="starter">Die Methode zum Starten der Operation.</param>
            protected QueueItem( Func<TAsyncControlType> starter )
            {
                // Remember
                m_starter = starter;
            }

            /// <summary>
            /// Führt die Operation aus.
            /// </summary>
            /// <param name="control">Die Steuereinheit der Operation.</param>
            protected abstract void OnRun( TAsyncControlType control );

            /// <summary>
            /// Führt die Operation aus.
            /// </summary>
            /// <param name="whenDone">Wird nach Abschluss aufgerufen.</param>
            public void Run( Action whenDone )
            {
                // Once only
                if (Interlocked.Exchange( ref m_running, 1 ) == 1)
                    return;

                // Save extract the controller
                TAsyncControlType controller;
                try
                {
                    // Process
                    controller = m_starter();
                }
                catch (Exception e)
                {
                    // Report
                    Trace.TraceError( e.Message );

                    // Report end - even if we did nothing (should be improved for production code)
                    whenDone();

                    // Did it
                    return;
                }

                // Wait
                ThreadPool.QueueUserWorkItem( control =>
                {
                    // Be very safe
                    try
                    {
                        // Execute the one item
                        OnRun( (TAsyncControlType) control );
                    }
                    catch (Exception e)
                    {
                        // Just report
                        Trace.TraceError( e.Message );
                    }

                    // Check for the next item
                    whenDone();
                }, controller );
            }
        }

        /// <summary>
        /// Ein Eintrag in der Warteschlange.
        /// </summary>
        /// <typeparam name="TResult">Die Art der Ergebnisdaten.</typeparam>
        private class QueueItem<TResult> : QueueItem
        {
            /// <summary>
            /// Das zugehörige Gerät.
            /// </summary>
            private readonly RecordingDevice<TSourceType, TAsyncControlType> m_device;

            /// <summary>
            /// Die Bearbeitung des Ergebnisses.
            /// </summary>
            private readonly Action<TResult> m_processor;

            /// <summary>
            /// Erstellt einen neuen Eintrag in der Warteschlange.
            /// </summary>
            /// <param name="starter">Die Methode zum Starten der Operation.</param>
            /// <param name="processor">Die Methode zur Bearbeitung des Ergebnisses.</param>
            /// <param name="device">Das zugehörige Gerät.</param>
            public QueueItem( Func<TAsyncControlType> starter, Action<TResult> processor, RecordingDevice<TSourceType, TAsyncControlType> device )
                : base( starter )
            {
                // Remember
                m_processor = processor;
                m_device = device;
            }

            /// <summary>
            /// Führt die Operation aus.
            /// </summary>
            /// <param name="control">Die Steuereinheit der Operation.</param>
            protected override void OnRun( TAsyncControlType control )
            {
                // Forward
                m_processor( m_device.GetResult<TResult>( control ) );
            }
        }

        /// <summary>
        /// Alle aktuellen Aufzeichnungsströme.
        /// </summary>
        private Dictionary<Guid, StreamInformation> m_streams = new Dictionary<Guid, StreamInformation>();

        /// <summary>
        /// Die Warteschlange.
        /// </summary>
        private readonly ConcurrentQueue<QueueItem> m_queue = new ConcurrentQueue<QueueItem>();

        /// <summary>
        /// Erstellt eine neue Simulation.
        /// </summary>
        /// <param name="name">Der Name des Gerätes.</param>
        /// <param name="priority">Die Priorität des Gerätes - kleiner Werte sind besser.</param>
        protected RecordingDevice( string name, int priority )
            : base( name, priority )
        {
        }

        /// <summary>
        /// Prüft, ob zwei Quellen zum gleichen Zeitpunkt aufgezeichnet werden können.
        /// </summary>
        /// <param name="first">Eine Quelle.</param>
        /// <param name="second">Eine andere Quelle.</param>
        /// <returns>Gesetzt, wenn eine gleichzeitige Aufzeichnung möglich ist.</returns>
        protected abstract bool CanRecordedInParallel( TSourceType first, TSourceType second );

        /// <summary>
        /// Ermittelt zu einer asynchronen Operation das Ergebnis.
        /// </summary>
        /// <typeparam name="TResult">Die Art des Ergebnisses.</typeparam>
        /// <param name="control">Die Steuerinformation der Operation.</param>
        /// <returns>Das gewünschte Ergebnis.</returns>
        protected abstract TResult GetResult<TResult>( TAsyncControlType control );

        /// <summary>
        /// Wird aufgerufen, um das zugehörige Gerät zu reservieren.
        /// </summary>
        protected abstract void ReserveDevice();

        /// <summary>
        /// Gibt die Nutzung des Gerätes endültig auf.
        /// </summary>
        protected abstract void ReleaseDevice();

        /// <summary>
        /// Wählt den Empfang einer Quelle an.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        protected abstract void Tune( TSourceType source );

        /// <summary>
        /// Beginnt mit der Aufzeichnung einer Quelle.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        /// <param name="recordingPath">Der volle Pfad zur Aufzeichnungsdatei.</param>
        protected abstract void BeginRecording( TSourceType source, Guid streamIdentifier, string recordingPath );

        /// <summary>
        /// Beendet die Aufzeichnung einer Quelle.
        /// </summary>
        /// <param name="source">Die gewünschte Quelle.</param>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        /// <param name="recordingPath">Der volle Pfad zur Aufzeichnungsdatei.</param>
        protected abstract void EndRecording( TSourceType source, Guid streamIdentifier, string recordingPath );

        /// <summary>
        /// Ermittelt zu einem Text den Sender.
        /// </summary>
        /// <param name="channelIdentification">Die Identifikation des Senders.</param>
        /// <returns>Die Senderinformationen.</returns>
        protected abstract TSourceType[] OnResolve( string channelIdentification );

        /// <summary>
        /// Ermittelt zu einem Text den Sender.
        /// </summary>
        /// <param name="channelIdentification">Die Identifikation des Senders.</param>
        /// <returns>Die Senderinformationen.</returns>
        public sealed override RecordingDevice.SourceResolveResult Resolve( string channelIdentification )
        {
            // Forward
            var sources = OnResolve( channelIdentification );
            if (sources.Length < 1)
                return null;
            else
                return new SourceResolveResult( sources );
        }

        /// <summary>
        /// Prüft, ob Sender gemeinsam aufgezeichnet werden können.
        /// </summary>
        /// <param name="left">Die Daten eines Senders.</param>
        /// <param name="right">Die Daten eines anderen Senders.</param>
        /// <returns>Gesetzt, wenn eine gemeinsame Aufzeichnung möglich ist.</returns>
        public override bool CheckForCommonSourceGroups( RecordingDevice.SourceResolveResult left, RecordingDevice.SourceResolveResult right )
        {
            // Convert
            var first = left as SourceResolveResult;
            if (first == null)
                return false;
            var second = right as SourceResolveResult;
            if (second == null)
                return false;

            // Check - actually a very brute and slow check if a single channel indeed maps to multiple sources
            return first.Sources.Any( firstSource => second.Sources.Any( secondSource => CanRecordedInParallel( firstSource, secondSource ) ) );
        }

        /// <summary>
        /// Beginnt mit der Verarbeitung einer asynchronen Operation.
        /// </summary>
        /// <param name="starter">Beginnt die Operation.</param>
        protected void Enqueue( Func<TAsyncControlType> starter )
        {
            // Forward
            Enqueue( starter, ( object result ) => { } );
        }

        /// <summary>
        /// Beginnt mit der Verarbeitung einer asynchronen Operation.
        /// </summary>
        /// <typeparam name="TResult">Die Art des Ergebnisses.</typeparam>
        /// <param name="starter">Beginnt die Operation.</param>
        /// <param name="processor">Wird mit Abschluss der Operation aufgerufen.</param>
        protected void Enqueue<TResult>( Func<TAsyncControlType> starter, Action<TResult> processor )
        {
            // Create new entry
            m_queue.Enqueue( new QueueItem<TResult>( starter, processor, this ) );

            // Process next
            ProcessQueue();
        }

        /// <summary>
        /// Führt den nächsten Eintrag der Warteschlange aus.
        /// </summary>
        private void ProcessQueue()
        {
            // Read item
            QueueItem queueItem;
            if (!m_queue.TryPeek( out queueItem ))
                return;

            // Process
            queueItem.Run( () =>
                {
                    // Remove the item we peeked
                    QueueItem doneItem;
                    m_queue.TryDequeue( out doneItem );

                    // Next
                    ProcessQueue();
                } );
        }

        /// <summary>
        /// Beginnt eine neue Aufzeichnung.
        /// </summary>
        /// <param name="source">Der zu verwendende Sender.</param>
        /// <param name="path">Der volle Pfad zur Aufzeichnungsdatei.</param>
        /// <returns>Gesetzt, wenn ein Start möglich war.</returns>
        public override Guid? Start( RecordingDevice.SourceResolveResult source, string path )
        {
            // Change type - if someone tries to trick us with a wrong type he will got some cast exception
            var resolve = (SourceResolveResult) source;

            // Source to use
            TSourceType newSource;

            // See if are idle - caller must guarentee thread safetiness
            if (m_streams.Count < 1)
            {
                // Just take the first
                newSource = resolve.Sources[0];
            }
            else
            {
                // Get some representative
                var representative = m_streams.Values.First().Source;

                // Try to find a matching source
                newSource = resolve.Sources.FirstOrDefault( possible => CanRecordedInParallel( possible, representative ) );

                // Wrong group
                if (newSource == null)
                    return null;
            }

            // Create new identifier
            var streamIdentifier = Guid.NewGuid();

            // Make sure that hardware is us
            ReserveDevice();

            // Tune on first request - actually request should be queued
            if (m_streams.Count < 1)
                Tune( newSource );

            // Make sure the path exists
            Directory.CreateDirectory( Path.GetDirectoryName( path ) );

            // Start this one
            BeginRecording( newSource, streamIdentifier, path );

            // Remember new information
            m_streams.Add( streamIdentifier, new StreamInformation( newSource, path ) );

            // Report
            return streamIdentifier;
        }

        /// <summary>
        /// Beendet eine Aufzeichnung.
        /// </summary>
        /// <param name="streamIdentifier">Die eindeutige Kennung der Aufzeichnung.</param>
        public override void Stop( Guid streamIdentifier )
        {
            // Locate
            StreamInformation info;
            if (!m_streams.TryGetValue( streamIdentifier, out info ))
                return;

            // Just correct
            m_streams.Remove( streamIdentifier );

            // Finish - will enqueue operation
            EndRecording( info.Source, streamIdentifier, info.RecordingPath );

            // There is more left
            if (m_streams.Count > 0)
                return;

            // Can release hardware - at least after queue is empty
            ReleaseDevice();
        }
    }
}
